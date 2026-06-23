namespace Pointer.Application.Response;

public class Result
{
    public bool IsSuccess { get; protected init; }
    public bool IsNotFound { get; protected init; }
    public bool IsConflict { get; protected init; }
    public string? Message { get; protected init; }

    public static Result Success(string? msg = null) => new() { IsSuccess = true, Message = msg };
    public static Result Failure(string msg) => new() { IsSuccess = false, Message = msg };
    public static Result NotFound(string msg) => new() { IsNotFound = true, Message = msg };
    public static Result Conflict(string msg) => new() { IsConflict = true, Message = msg };
}

public class Result<T> : Result
{
    public T? Data { get; private init; }
    public static Result<T> Success(T data, string? msg = null) =>
        new() { IsSuccess = true, Data = data, Message = msg };
    public new static Result<T> Failure(string msg) => new() { IsSuccess = false, Message = msg };
    public new static Result<T> NotFound(string msg) => new() { IsNotFound = true, Message = msg };
    public new static Result<T> Conflict(string msg) => new() { IsConflict = true, Message = msg };
}
