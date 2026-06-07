using System;

namespace PdfEngine.Application.Common;

public class Result<T>
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T? Value { get; }
    public Error Error { get; }

    private Result(bool isSuccess, T? value, Error error)
    {
        if (isSuccess && error.Code != string.Empty)
            throw new InvalidOperationException();
        if (!isSuccess && error.Code == string.Empty)
            throw new InvalidOperationException();

        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public static Result<T> Success(T value) => new(true, value, Error.None);
    public static Result<T> Fail(Error error) => new(false, default, error);
}
