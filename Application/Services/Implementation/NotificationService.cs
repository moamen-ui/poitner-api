using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Notification;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class NotificationService(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IMembershipService? memberships = null
) : INotificationService
{
    private readonly IUnitOfWork _unitOfWork = unitOfWork;
    private readonly ICurrentUser _currentUser = currentUser;
    private readonly IMembershipService _memberships = memberships ?? new MembershipService(unitOfWork);

    private IQueryable<Notification> MyNotifications()
    {
        var callerId = _currentUser.Id ?? Guid.Empty;
        return _unitOfWork.Repository<Notification>()
            .Query()
            .Where(n => n.UserId == callerId && n.DeletedAt == null);
    }

    public async Task EnqueueAsync(Notification notification)
    {
        if (notification.OwnerId == null)
        {
            notification.OwnerId = TenantStamp.OwnerFor(_currentUser);
        }

        // DB-11a: a notification is only worth queuing for a recipient who can still see it — a
        // live, active, Approved membership in this workspace. Ended/disabled/pending memberships
        // (or no membership at all) silently skip rather than piling up unreadable notifications.
        if (notification.OwnerId is Guid owner)
        {
            var recipient = await _memberships.FindIdentityByPublicIdAsync(notification.UserId);
            var membership = recipient != null
                ? await _memberships.GetMembershipAsync(recipient.Id, owner)
                : null;
            if (membership == null || !membership.IsActive || membership.ApprovalStatus != Domain.Enums.ApprovalStatus.Approved)
                return;
        }

        await _unitOfWork.Repository<Notification>().AddAsync(notification);
    }

    public async Task<Result<PagedData<NotificationDto>>> ListAsync(bool? unread = null, int page = 1, int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;

        var query = MyNotifications()
            .Include(n => n.Comment)
                .ThenInclude(c => c!.Project)
            .Include(n => n.Project)
            .Include(n => n.Suggestion)
            .AsNoTracking();

        if (unread == true)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var dtos = items.Select(MapToDto).ToList();
        var pagination = new Pagination
        {
            PageNumber = page,
            PageSize = pageSize,
            TotalItems = total,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize)
        };

        return Result<PagedData<NotificationDto>>.Success(new PagedData<NotificationDto>(dtos, pagination));
    }

    public async Task<Result<UnreadCountResponse>> GetUnreadCountAsync()
    {
        var count = await MyNotifications()
            .Where(n => n.ReadAt == null)
            .CountAsync();

        return Result<UnreadCountResponse>.Success(new UnreadCountResponse { Count = count });
    }

    public async Task<Result<NotificationDto>> MarkReadAsync(int id)
    {
        var notification = await MyNotifications()
            .Include(n => n.Comment)
                .ThenInclude(c => c!.Project)
            .Include(n => n.Project)
            .Include(n => n.Suggestion)
            .FirstOrDefaultAsync(n => n.Id == id);

        if (notification == null)
            return Result<NotificationDto>.NotFound("Notification not found.");

        if (notification.ReadAt == null)
        {
            notification.ReadAt = DateTime.UtcNow;
            _unitOfWork.Repository<Notification>().Update(notification);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result<NotificationDto>.Success(MapToDto(notification));
    }

    public async Task<Result<ReadAllNotificationsResponse>> MarkAllReadAsync()
    {
        var unread = await MyNotifications()
            .Where(n => n.ReadAt == null)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var n in unread)
        {
            n.ReadAt = now;
            _unitOfWork.Repository<Notification>().Update(n);
        }

        if (unread.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync();
        }

        return Result<ReadAllNotificationsResponse>.Success(new ReadAllNotificationsResponse { Marked = unread.Count });
    }

    private static NotificationDto MapToDto(Notification n)
    {
        // Old clients render CommentBodyExcerpt regardless of notification kind — when there is no
        // Comment (suggestion-flow notifications), fall back to the suggestion's text so they still
        // show something meaningful.
        var body = n.Comment != null ? n.Comment.Body : n.Suggestion?.Text ?? string.Empty;
        var excerpt = body.Length > 80 ? body[..80] : body;

        NotificationPayloadDto? payload = null;
        if (!string.IsNullOrWhiteSpace(n.Payload))
        {
            try
            {
                payload = JsonSerializer.Deserialize<NotificationPayloadDto>(n.Payload, (JsonSerializerOptions?)null);
            }
            catch
            {
                // Fallback for non-JSON or malformed payload
            }
        }

        return new NotificationDto
        {
            Id = n.Id,
            Type = n.Type,
            CommentId = n.CommentId,
            SuggestionId = n.SuggestionId,
            ProjectKey = n.Project?.Key ?? n.Comment?.Project?.Key ?? string.Empty,
            ProjectName = n.Project?.Name ?? n.Comment?.Project?.Name ?? string.Empty,
            CommentBodyExcerpt = excerpt,
            Payload = payload,
            CreatedAt = n.CreatedAt,
            ReadAt = n.ReadAt
        };
    }
}
