using AegisOps.Application.Audit;
using AegisOps.Domain.Audit;
using AegisOps.Infrastructure.Persistence;

namespace AegisOps.Infrastructure.Audit;

public sealed class EfAuditWriter(AegisOpsDbContext db) : IAuditWriter {
    public void Write(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);
}
