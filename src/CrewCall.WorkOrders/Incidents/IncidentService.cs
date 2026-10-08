using CrewCall.WorkOrders.Operations;
using CrewCall.WorkOrders.Visits;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.WorkOrders.Incidents;

/// <summary>
/// Incidents and their lifecycle (ADR-0012). Analysis and dispatch are driven by Scheduling, which reaches the operations
/// below through its port; this service owns the incident's state, its events and the work order and visit that
/// dispatch creates. Every change is guarded by the incident's row version, so concurrent changes cannot both win.
/// </summary>
public sealed class IncidentService(IWorkOrdersDbContext db, TimeProvider clock)
{
    public async Task<CreateIncidentOutcome> CreateAsync(CreateIncident command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        if (command.CustomerId == Guid.Empty)
        {
            errors.Add("customerId", "Required.");
        }

        if (command.SiteId == Guid.Empty)
        {
            errors.Add("siteId", "Required.");
        }

        var title = errors.Required("title", command.Title, Incident.TitleMaxLength);
        var description = errors.Optional("description", command.Description, Incident.DescriptionMaxLength);
        var priority = errors.EnumValue<IncidentPriority>("priority", command.Priority);

        if (command.RequestedStart is null)
        {
            errors.Add("requestedStart", "Required.");
        }

        if (command.RequestedEnd is null)
        {
            errors.Add("requestedEnd", "Required.");
        }

        // Validate the stored (UTC, microsecond) values, so values differing only below a microsecond cannot become an
        // empty interval.
        DateTimeOffset? start = command.RequestedStart is { } requestedStart ? StoredTime.Normalize(requestedStart) : null;
        DateTimeOffset? end = command.RequestedEnd is { } requestedEnd ? StoredTime.Normalize(requestedEnd) : null;
        if (start is not null && end is not null)
        {
            if (start >= end)
            {
                errors.Add("requestedEnd", "Must be after requestedStart.");
            }
            else if (end - start > Incident.MaxDuration)
            {
                errors.Add("requestedEnd", $"The requested window can be at most {Incident.MaxDuration.TotalDays:0} days long.");
            }
        }

        var skillCodes = NormalizeSkillCodes(errors, command.RequiredSkillCodes);

        if (errors.Any)
        {
            return new CreateIncidentOutcome.Invalid(errors.ToDictionary());
        }

        if (!await db.Customers.AnyAsync(customer => customer.Id == command.CustomerId, cancellationToken))
        {
            return new CreateIncidentOutcome.CustomerNotFound(command.CustomerId);
        }

        var siteCustomerId = await db.Sites
            .Where(site => site.Id == command.SiteId)
            .Select(site => (Guid?)site.CustomerId)
            .SingleOrDefaultAsync(cancellationToken);

        if (siteCustomerId is null)
        {
            return new CreateIncidentOutcome.SiteNotFound(command.SiteId);
        }

        if (siteCustomerId != command.CustomerId)
        {
            errors.Add("siteId", "The site does not belong to the customer.");
            return new CreateIncidentOutcome.Invalid(errors.ToDictionary());
        }

        var now = StoredTime.UtcNow(clock);
        var incident = new Incident(
            Guid.CreateVersion7(), command.CustomerId, command.SiteId, title!, description, priority!.Value, start!.Value, end!.Value, skillCodes, now);

        db.Incidents.Add(incident);
        db.AppendOperationalEvent(
            IncidentEvents.IncidentCreated,
            IncidentEvents.IncidentAggregate,
            incident.Id,
            now,
            new IncidentCreatedPayload(
                incident.Id, incident.CustomerId, incident.SiteId, incident.Priority, incident.RequestedStart, incident.RequestedEnd, skillCodes));

        // One SaveChanges: the incident, its skills and its event are written in a single transaction.
        await db.SaveChangesAsync(cancellationToken);

        return new CreateIncidentOutcome.Created(incident);
    }

    /// <summary>Newest first (CreatedAtUtc descending, then id), optionally filtered by status and priority.</summary>
    public async Task<ListIncidentsOutcome> ListAsync(ListIncidents query, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var status = errors.OptionalEnumValue<IncidentStatus>("status", query.Status);
        var priority = errors.OptionalEnumValue<IncidentPriority>("priority", query.Priority);

        if (errors.Any)
        {
            return new ListIncidentsOutcome.Invalid(errors.ToDictionary());
        }

        var incidents = db.Incidents.AsNoTracking().Include(incident => incident.RequiredSkills).AsQueryable();
        if (status is { } statusFilter)
        {
            incidents = incidents.Where(incident => incident.Status == statusFilter);
        }

        if (priority is { } priorityFilter)
        {
            incidents = incidents.Where(incident => incident.Priority == priorityFilter);
        }

        return new ListIncidentsOutcome.Listed(await incidents
            .OrderByDescending(incident => incident.CreatedAtUtc)
            .ThenBy(incident => incident.Id)
            .ToListAsync(cancellationToken));
    }

    public Task<Incident?> GetAsync(Guid incidentId, CancellationToken cancellationToken) =>
        db.Incidents
            .AsNoTracking()
            .Include(incident => incident.RequiredSkills)
            .SingleOrDefaultAsync(incident => incident.Id == incidentId, cancellationToken);

    /// <summary>
    /// Starts an analysis: New → Analyzing. An incident already Analyzing or ReadyForDispatch can be analysed again (the
    /// plan may have changed) and keeps its status. Dispatched, Resolved and Cancelled incidents cannot.
    /// </summary>
    public Task<ChangeIncidentOutcome> BeginAnalysisAsync(Guid incidentId, CancellationToken cancellationToken) =>
        ChangeAsync(incidentId, IncidentStatus.Analyzing, cancellationToken, (incident, now) =>
            incident.Status is IncidentStatus.Analyzing or IncidentStatus.ReadyForDispatch
            || incident.TryTransitionTo(IncidentStatus.Analyzing, now));

    /// <summary>
    /// Completes an analysis: Analyzing → ReadyForDispatch (ReadyForDispatch stays), with the IncidentAnalyzed event.
    /// Fails when the incident was cancelled meanwhile.
    /// </summary>
    public Task<ChangeIncidentOutcome> CompleteAnalysisAsync(RecordIncidentAnalysis analysis, CancellationToken cancellationToken) =>
        ChangeAsync(analysis.IncidentId, IncidentStatus.ReadyForDispatch, cancellationToken, (incident, now) =>
        {
            if (incident.Status != IncidentStatus.ReadyForDispatch && !incident.TryTransitionTo(IncidentStatus.ReadyForDispatch, now))
            {
                return false;
            }

            db.AppendOperationalEvent(
                IncidentEvents.IncidentAnalyzed,
                IncidentEvents.IncidentAggregate,
                incident.Id,
                now,
                new IncidentAnalyzedPayload(
                    incident.Id,
                    incident.Status,
                    analysis.GeneratedAtUtc,
                    analysis.CandidatesEvaluated,
                    analysis.EligibleCount,
                    analysis.RejectedCount,
                    analysis.DirectAssignmentCount,
                    analysis.RequiresRescheduleCount,
                    analysis.UnavailableCount));
            return true;
        });

    /// <summary>
    /// Dispatched → Resolved, with ResolvedAtUtc and the IncidentResolved event. The work order and its visit are not
    /// completed: they follow their own lifecycle.
    /// </summary>
    public Task<ChangeIncidentOutcome> ResolveAsync(Guid incidentId, CancellationToken cancellationToken) =>
        ChangeAsync(incidentId, IncidentStatus.Resolved, cancellationToken, (incident, now) =>
        {
            if (!incident.TryResolve(now))
            {
                return false;
            }

            db.AppendOperationalEvent(
                IncidentEvents.IncidentResolved,
                IncidentEvents.IncidentAggregate,
                incident.Id,
                now,
                new IncidentResolvedPayload(incident.Id, incident.WorkOrderId, incident.ResolvedAtUtc!.Value));
            return true;
        });

    /// <summary>
    /// Cancels an incident that has not been dispatched. A dispatched incident cannot be cancelled here: that would
    /// leave, or silently cancel, its work order, visit and assignment (409 instead).
    /// </summary>
    public Task<ChangeIncidentOutcome> CancelAsync(Guid incidentId, CancellationToken cancellationToken) =>
        ChangeAsync(incidentId, IncidentStatus.Cancelled, cancellationToken, (incident, now) =>
        {
            var oldStatus = incident.Status;
            if (!incident.TryTransitionTo(IncidentStatus.Cancelled, now))
            {
                return false;
            }

            db.AppendOperationalEvent(
                IncidentEvents.IncidentCancelled,
                IncidentEvents.IncidentAggregate,
                incident.Id,
                now,
                new IncidentCancelledPayload(incident.Id, oldStatus));
            return true;
        });

    /// <summary>
    /// Adds the WorkOrders side of a dispatch to the unit of work: the work order (through the regular creation path,
    /// with its WorkOrderCreated event), its Planned visit over the requested window (with VisitCreated), the incident's
    /// move to Dispatched with the link to the work order, and the IncidentDispatched event. Nothing is saved: the caller
    /// commits it together with the assignment's resource claim, in one transaction, or not at all.
    /// </summary>
    /// <remarks>The incident is loaded tracked, so its row version guards the commit against a concurrent dispatch.</remarks>
    public async Task<StageIncidentDispatchOutcome> StageDispatchAsync(StageIncidentDispatch command, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == command.IncidentId, cancellationToken);
        if (incident is null)
        {
            return new StageIncidentDispatchOutcome.NotFound(command.IncidentId);
        }

        if (incident.WorkOrderId is { } existingWorkOrderId)
        {
            return new StageIncidentDispatchOutcome.AlreadyDispatched(incident.Id, existingWorkOrderId);
        }

        if (incident.Status != IncidentStatus.ReadyForDispatch)
        {
            return new StageIncidentDispatchOutcome.NotReady(incident.Id, incident.Status);
        }

        var now = StoredTime.UtcNow(clock);
        var workOrder = WorkOrderService.Add(
            db,
            incident.CustomerId,
            incident.SiteId,
            incident.Title,
            incident.Description,
            IncidentLifecycle.ToWorkOrderPriority(incident.Priority),
            now);
        var visit = VisitService.Add(
            db, command.VisitId, new VisitWorkOrder(workOrder.Id, workOrder.CustomerId, workOrder.SiteId, workOrder.Priority),
            incident.RequestedStart, incident.RequestedEnd, notes: null, now);

        if (!incident.TryDispatch(workOrder.Id, now))
        {
            throw new InvalidOperationException($"Incident '{incident.Id}' could not be dispatched from {incident.Status}.");
        }

        db.AppendOperationalEvent(
            IncidentEvents.IncidentDispatched,
            IncidentEvents.IncidentAggregate,
            incident.Id,
            now,
            new IncidentDispatchedPayload(
                incident.Id, workOrder.Id, visit.Id, command.AssignmentId, command.TechnicianId, command.VehicleId, command.EquipmentIds));

        return new StageIncidentDispatchOutcome.Staged(incident, workOrder, visit);
    }

    private async Task<ChangeIncidentOutcome> ChangeAsync(
        Guid incidentId, IncidentStatus target, CancellationToken cancellationToken, Func<Incident, DateTimeOffset, bool> change)
    {
        var incident = await db.Incidents
            .Include(i => i.RequiredSkills)
            .SingleOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
        if (incident is null)
        {
            return new ChangeIncidentOutcome.NotFound(incidentId);
        }

        var oldStatus = incident.Status;
        if (!change(incident, StoredTime.UtcNow(clock)))
        {
            return new ChangeIncidentOutcome.TransitionNotAllowed(oldStatus, target);
        }

        try
        {
            // One SaveChanges: the new status and its event are written in a single transaction.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Incidents carry a row version: a concurrent change won; neither status nor event was saved.
            return new ChangeIncidentOutcome.ConcurrentChange(incident.Id);
        }

        return new ChangeIncidentOutcome.Changed(incident, oldStatus);
    }

    /// <summary>Trimmed and upper case (as Workforce stores skill codes); blank and duplicate codes are rejected.</summary>
    private static List<string> NormalizeSkillCodes(ValidationErrors errors, IReadOnlyCollection<string?>? codes)
    {
        var normalized = new List<string>();
        foreach (var code in codes ?? [])
        {
            var text = code?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(text))
            {
                errors.Add("requiredSkillCodes", "Must not contain blank codes.");
            }
            else if (text.Length > Incident.SkillCodeMaxLength)
            {
                errors.Add("requiredSkillCodes", $"Each code can be at most {Incident.SkillCodeMaxLength} characters.");
            }
            else if (normalized.Contains(text))
            {
                errors.Add("requiredSkillCodes", $"Must not contain duplicates ('{text}').");
            }
            else
            {
                normalized.Add(text);
            }
        }

        if (normalized.Count > Incident.MaxRequiredSkills)
        {
            errors.Add("requiredSkillCodes", $"At most {Incident.MaxRequiredSkills} codes.");
        }

        return normalized;
    }
}
