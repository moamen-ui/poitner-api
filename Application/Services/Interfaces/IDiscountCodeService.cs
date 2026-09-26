using Pointer.Application.DTOs.DiscountCode;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>DB-20 §3.6h. Super-admin CRUD for reference/discount codes — create (immutable code),
/// update every field but <c>code</c>, deactivate instead of delete (F-B8), list with usage counts,
/// and a code's redemption drill-down.</summary>
public interface IDiscountCodeService
{
    Task<Result<List<DiscountCodeResponse>>> ListAsync(string? sort, bool? active);
    Task<Result<DiscountCodeResponse>> GetAsync(int id);
    Task<Result<DiscountCodeResponse>> CreateAsync(CreateDiscountCodeRequest request);
    Task<Result<DiscountCodeResponse>> UpdateAsync(int id, UpdateDiscountCodeRequest request);
    Task<Result<List<DiscountRedemptionResponse>>> GetRedemptionsAsync(int id);
}
