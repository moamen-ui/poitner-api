using Pointer.Application.DTOs.Notification;
using Pointer.Application.Response;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Interfaces;

public interface INotificationService
{
    Task EnqueueAsync(Notification notification);
    Task<Result<PagedData<NotificationDto>>> ListAsync(bool? unread = null, int page = 1, int pageSize = 20);
    Task<Result<UnreadCountResponse>> GetUnreadCountAsync();
    Task<Result<NotificationDto>> MarkReadAsync(int id);
    Task<Result<ReadAllNotificationsResponse>> MarkAllReadAsync();
}
