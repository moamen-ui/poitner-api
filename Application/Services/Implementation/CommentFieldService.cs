using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Application.Validators;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Admin-defined comment fields (R4-01) — single source of truth for the A1 definition rules and
/// the value rules. Pure methods (ValidateValues/ValidateDefinitions/Resolve) are unit-testable
/// without a database; the two workspace endpoints mirror AiRuleService.CreateAsync's ownership
/// guards exactly (super admins and quick-access users have no workspace of their own here).
/// </summary>
public class CommentFieldService : ICommentFieldService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public CommentFieldService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<List<CommentFieldDefinition>> GetDefinitionsForOwnerAsync(Guid? ownerId, bool enabledOnly, CancellationToken ct = default)
    {
        // Null owner = a super-admin-created project: no workspace, no definitions.
        if (ownerId is null)
            return new List<CommentFieldDefinition>();

        // IgnoreQueryFilters is allowed HERE ONLY (R4-01 B): the caller resolved the owner
        // server-side from the project/comment, and the widget user may be a quick-access user of
        // that workspace. The explicit OwnerId predicate remains the real scope.
        var row = await _unitOfWork.Repository<WorkspaceSetting>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.DeletedAt == null)
            .FirstOrDefaultAsync(ct);

        var defs = row?.CommentFieldDefinitions ?? new List<CommentFieldDefinition>();
        return defs
            .Where(d => !enabledOnly || d.Enabled)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Key)
            .ToList();
    }

    public Result<Dictionary<string, string>> ValidateValues(IReadOnlyList<CommentFieldDefinition> defs, Dictionary<string, string>? input)
    {
        if (input is not { Count: > 0 })
            return Result<Dictionary<string, string>>.Success(new Dictionary<string, string>());

        var byKey = defs.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, raw) in input)
        {
            // Trim; a value that trims to empty REMOVES the key (unset) — never stored.
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0)
                continue;

            if (!byKey.TryGetValue(key, out var def))
                return Result<Dictionary<string, string>>.Failure($"Unknown comment field '{key}'.");
            if (!def.Enabled)
                return Result<Dictionary<string, string>>.Failure($"Comment field '{def.Label}' is disabled.");

            if (def.Type == CommentFieldType.Text)
            {
                if (value.Length > 500)
                    return Result<Dictionary<string, string>>.Failure($"'{def.Label}' must be at most 500 characters.");
            }
            else if (def.Type == CommentFieldType.Url)
            {
                // Absolute http(s) only, a real host, NO userinfo (rejects https://user@host),
                // length cap. uri.Host is already lower-case.
                if (value.Length > 2000
                    || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || (uri.Scheme != "http" && uri.Scheme != "https")
                    || string.IsNullOrEmpty(uri.Host)
                    || !string.IsNullOrEmpty(uri.UserInfo))
                    return Result<Dictionary<string, string>>.Failure($"'{def.Label}' must be a valid http(s) link.");

                if (def.AllowedHosts is { Count: > 0 } hosts && !hosts.Any(p => HostMatches(uri.Host, p)))
                    return Result<Dictionary<string, string>>.Failure($"'{def.Label}' must be a link on {string.Join(", ", hosts)}.");
            }
            else if (def.Type == CommentFieldType.Select)
            {
                if (!def.Options.Contains(value, StringComparer.Ordinal))
                    return Result<Dictionary<string, string>>.Failure($"'{def.Label}' must be one of: {string.Join(", ", def.Options)}.");
            }
            else
            {
                return Result<Dictionary<string, string>>.Failure($"Unknown comment field '{key}'.");
            }

            result[key] = value;
        }

        if (JsonSerializer.Serialize(result).Length > 4000)
            return Result<Dictionary<string, string>>.Failure("Comment fields are too large.");

        return Result<Dictionary<string, string>>.Success(result);
    }

    /// <summary>
    /// `*.example.com` matches any host ending in `.example.com` OR equal to `example.com`;
    /// anything else matches its host exactly (ordinal, both sides already lower-case).
    /// </summary>
    private static bool HostMatches(string host, string pattern)
    {
        if (string.Equals(host, pattern, StringComparison.Ordinal))
            return true;
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var bare = pattern[2..];
            return host.EndsWith("." + bare, StringComparison.Ordinal)
                || string.Equals(host, bare, StringComparison.Ordinal);
        }
        return false;
    }

    public Result<List<CommentFieldDefinition>> ValidateDefinitions(List<CommentFieldDefinitionDto> dtos)
    {
        if (dtos.Count > UpdateCommentFieldDefinitionsValidator.MaxDefinitions)
            return Result<List<CommentFieldDefinition>>.Failure($"A workspace allows at most {UpdateCommentFieldDefinitionsValidator.MaxDefinitions} comment fields.");

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CommentFieldDefinition>(dtos.Count);

        foreach (var dto in dtos)
        {
            var key = dto.Key?.Trim() ?? string.Empty;
            if (!Regex.IsMatch(key, UpdateCommentFieldDefinitionsValidator.KeyPattern))
                return Result<List<CommentFieldDefinition>>.Failure($"Invalid comment field key '{dto.Key}'.");
            if (!seenKeys.Add(key))
                return Result<List<CommentFieldDefinition>>.Failure($"Duplicate comment field key '{key}'.");

            // Single line, 1-40 chars — the label is printed OUTSIDE the untrusted fence in the
            // AI apply prompt, so it must never carry a newline.
            var label = dto.Label?.Trim() ?? string.Empty;
            if (!Regex.IsMatch(label, UpdateCommentFieldDefinitionsValidator.LabelPattern))
                return Result<List<CommentFieldDefinition>>.Failure($"Invalid label for '{key}'.");

            if (!Enum.IsDefined(typeof(CommentFieldType), dto.Type))
                return Result<List<CommentFieldDefinition>>.Failure($"Invalid type for '{key}'.");

            var options = (dto.Options ?? new List<string>()).Select(o => o.Trim()).ToList();
            var hosts = (dto.AllowedHosts ?? new List<string>()).Select(h => h.Trim()).ToList();

            if (dto.Type != CommentFieldType.Select && options.Count > 0)
                return Result<List<CommentFieldDefinition>>.Failure($"Options are only allowed on select fields ('{key}').");
            if (dto.Type == CommentFieldType.Select && (options.Count < 1 || options.Count > 20))
                return Result<List<CommentFieldDefinition>>.Failure($"A select field needs 1-20 options ('{key}').");
            if (options.Any(o => o.Length is < 1 or > 40))
                return Result<List<CommentFieldDefinition>>.Failure($"Each option must be 1-40 characters ('{key}').");
            if (options.Distinct(StringComparer.Ordinal).Count() != options.Count)
                return Result<List<CommentFieldDefinition>>.Failure($"Options must be distinct ('{key}').");

            if (dto.Type != CommentFieldType.Url && hosts.Count > 0)
                return Result<List<CommentFieldDefinition>>.Failure($"Allowed hosts are only allowed on link fields ('{key}').");
            if (hosts.Count > 10)
                return Result<List<CommentFieldDefinition>>.Failure($"At most 10 allowed hosts ('{key}').");
            if (hosts.Any(h => !Regex.IsMatch(h, UpdateCommentFieldDefinitionsValidator.HostPattern)))
                return Result<List<CommentFieldDefinition>>.Failure($"Hosts must be lower-case like example.com or *.example.com ('{key}').");

            var tool = string.IsNullOrWhiteSpace(dto.SuggestedTool) ? null : dto.SuggestedTool.Trim();
            if (tool != null && (tool.Length > 40 || !Regex.IsMatch(tool, UpdateCommentFieldDefinitionsValidator.SuggestedToolPattern)))
                return Result<List<CommentFieldDefinition>>.Failure($"Suggested tool must be a short lower-case slug like 'atlassian' ('{key}').");

            var hint = string.IsNullOrWhiteSpace(dto.Hint) ? null : dto.Hint.Trim();
            if (hint is { Length: > 120 })
                return Result<List<CommentFieldDefinition>>.Failure($"Hint must be at most 120 characters ('{key}').");

            result.Add(new CommentFieldDefinition
            {
                Key = key,
                Label = label,
                Type = dto.Type,
                Options = options,
                AllowedHosts = hosts,
                SuggestedTool = tool,
                Hint = hint,
                Enabled = dto.Enabled,
                // Re-normalised to 0..n-1 in the GIVEN order — the dashboard's ↑/↓ buttons send
                // the list already sorted; whatever SortOrder values arrive are ignored.
                SortOrder = result.Count
            });
        }

        return Result<List<CommentFieldDefinition>>.Success(result);
    }

    public List<CommentFieldValueDto> Resolve(IReadOnlyList<CommentFieldDefinition> defs, Dictionary<string, string>? values)
    {
        if (values is not { Count: > 0 })
            return new List<CommentFieldValueDto>();

        var ordered = defs.OrderBy(d => d.SortOrder).ThenBy(d => d.Key).ToList();
        var byKey = ordered.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var result = new List<CommentFieldValueDto>();

        // Defined fields first, in definition order (disabled definitions with a stored value are
        // returned too — the stakeholder's data stays visible).
        foreach (var def in ordered)
        {
            if (values.TryGetValue(def.Key, out var value))
                result.Add(new CommentFieldValueDto
                {
                    Key = def.Key,
                    Label = def.Label,
                    Type = def.Type,
                    Value = value,
                    SuggestedTool = def.SuggestedTool
                });
        }

        // Orphans last, alphabetically — never hide data the stakeholder typed just because its
        // definition was deleted or renamed.
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!byKey.ContainsKey(key))
                result.Add(new CommentFieldValueDto
                {
                    Key = key,
                    Label = key,
                    Type = CommentFieldType.Text,
                    Value = value,
                    SuggestedTool = null
                });
        }

        return result;
    }

    public async Task<Result<CommentFieldDefinitionsResponse>> GetDefinitionsAsync()
    {
        // Mirror AiRuleService.CreateAsync: super admins (platform-only, no workspace of their
        // own) and quick-access users never manage definitions — the dashboard hides the card.
        if (_currentUser.IsSuperAdmin)
            return Result<CommentFieldDefinitionsResponse>.Forbidden(MessageKeys.Common.Forbidden);
        if (_currentUser.IsQuickAccess)
            return Result<CommentFieldDefinitionsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
        if (ownerId is not Guid owner)
            return Result<CommentFieldDefinitionsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var defs = await GetDefinitionsForOwnerAsync(owner, enabledOnly: false);
        return Result<CommentFieldDefinitionsResponse>.Success(new CommentFieldDefinitionsResponse
        {
            Fields = defs.Select(CommentFieldDefinitionDto.FromDomain).ToList()
        });
    }

    public async Task<Result<CommentFieldDefinitionsResponse>> UpdateDefinitionsAsync(UpdateCommentFieldDefinitionsRequest request)
    {
        if (_currentUser.IsSuperAdmin)
            return Result<CommentFieldDefinitionsResponse>.Forbidden(MessageKeys.Common.Forbidden);
        if (_currentUser.IsQuickAccess)
            return Result<CommentFieldDefinitionsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
        if (ownerId is not Guid owner)
            return Result<CommentFieldDefinitionsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var validated = ValidateDefinitions(request.Fields ?? new List<CommentFieldDefinitionDto>());
        if (!validated.IsSuccess)
            return Result<CommentFieldDefinitionsResponse>.Failure(validated.Message!);

        // Upsert the workspace's one row, found by the explicit owner predicate (never a bare
        // FirstOrDefault — a super admin would otherwise get an arbitrary tenant's row).
        var row = await _unitOfWork.Repository<WorkspaceSetting>()
            .Query()
            .Where(x => x.OwnerId == owner && x.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (row == null)
        {
            row = new WorkspaceSetting
            {
                OwnerId = owner,
                CommentFieldDefinitions = validated.Data!
            };
            await _unitOfWork.Repository<WorkspaceSetting>().AddAsync(row);
        }
        else
        {
            row.CommentFieldDefinitions = validated.Data!;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = _currentUser.Id;
            _unitOfWork.Repository<WorkspaceSetting>().Update(row);
        }
        await _unitOfWork.SaveChangesAsync();

        return Result<CommentFieldDefinitionsResponse>.Success(new CommentFieldDefinitionsResponse
        {
            Fields = validated.Data!.Select(CommentFieldDefinitionDto.FromDomain).ToList()
        });
    }
}
