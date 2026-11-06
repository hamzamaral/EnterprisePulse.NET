namespace EnterprisePulse.Core.Common;

public abstract class AppException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class BadRequestAppException(string message) : AppException(400, message);

public sealed class UnauthorizedAppException(string message = "Oturum açmanız gerekiyor") : AppException(401, message);

public sealed class ForbiddenAppException(string message = "Bu işlem için yetkiniz yok") : AppException(403, message);

public sealed class NotFoundAppException(string message = "Kayıt bulunamadı") : AppException(404, message);

public sealed class ConflictAppException(string message) : AppException(409, message);

public static class Guard
{
    public static T Found<T>(T? value, string message = "Kayıt bulunamadı") where T : class =>
        value ?? throw new NotFoundAppException(message);

    public static void That(bool condition, string message)
    {
        if (!condition) throw new BadRequestAppException(message);
    }

    public static void State(bool condition, string message)
    {
        if (!condition) throw new ConflictAppException(message);
    }
}

public sealed record PageMeta(int Page, int PageSize, int Total, int TotalPages);

public sealed record Paged<T>(IReadOnlyList<T> Data, PageMeta Meta)
{
    public static Paged<T> Of(IReadOnlyList<T> data, int total, int page, int pageSize) =>
        new(data, new PageMeta(page, pageSize, total, pageSize == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize)));
}
