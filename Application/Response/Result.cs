namespace Pointer.Application.Response;

/// <summary>
/// Details of a plan-limit hit, surfaced on <see cref="Result.Limit"/> when
/// <see cref="Result.IsLimitReached"/> is true. Clients detect the flag (never string-match the
/// message) to render "3/5 projects — upgrade".
/// </summary>
public sealed record PlanLimit(string Lever, int Current, int Limit, int PlanId);

public class Result
{
    public bool IsSuccess { get; protected init; }
    public bool IsNotFound { get; protected init; }
    public bool IsConflict { get; protected init; }
    public bool IsForbidden { get; protected init; }

    /// <summary>Set when a plan entitlement limit blocked the action. Returned as a 400 (BadRequest).</summary>
    public bool IsLimitReached { get; protected init; }

    /// <summary>Populated only when <see cref="IsLimitReached"/> is true.</summary>
    public PlanLimit? Limit { get; protected init; }

    public string? Message { get; protected init; }

    public static Result Success(string? msg = null) => new() { IsSuccess = true, Message = msg };
    public static Result Failure(string msg) => new() { IsSuccess = false, Message = msg };
    public static Result NotFound(string msg) => new() { IsNotFound = true, Message = msg };
    public static Result Conflict(string msg) => new() { IsConflict = true, Message = msg };
    public static Result Forbidden(string msg) => new() { IsForbidden = true, Message = msg };
    public static Result LimitReached(string msg, PlanLimit limit) =>
        new() { IsSuccess = false, IsLimitReached = true, Limit = limit, Message = msg };
}

public class Result<T> : Result
{
    public T? Data { get; private init; }
    public static Result<T> Success(T data, string? msg = null) =>
        new() { IsSuccess = true, Data = data, Message = msg };
    public new static Result<T> Failure(string msg) => new() { IsSuccess = false, Message = msg };
    public static Result<T> Failure(string msg, T data) => new() { IsSuccess = false, Message = msg, Data = data };
    public new static Result<T> NotFound(string msg) => new() { IsNotFound = true, Message = msg };
    public new static Result<T> Conflict(string msg) => new() { IsConflict = true, Message = msg };
    public new static Result<T> Forbidden(string msg) => new() { IsForbidden = true, Message = msg };
    public new static Result<T> LimitReached(string msg, PlanLimit limit) =>
        new() { IsSuccess = false, IsLimitReached = true, Limit = limit, Message = msg };
}
