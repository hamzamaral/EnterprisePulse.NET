namespace EnterprisePulse.Core.Data;

public interface IEntity
{
    Guid Id { get; set; }
}

/// <summary>Rows owned by one company. The DbContext filters and stamps them automatically.</summary>
public interface ITenantEntity
{
    Guid CompanyId { get; set; }
}

public interface ISoftDelete
{
    DateTime? DeletedAt { get; set; }
}

public interface IUpdatedAt
{
    DateTime UpdatedAt { get; set; }
}
