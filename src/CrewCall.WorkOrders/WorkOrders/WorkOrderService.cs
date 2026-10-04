using CrewCall.WorkOrders.Operations;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.WorkOrders;

public sealed class WorkOrderService(IWorkOrdersDbContext db, TimeProvider clock)
{
    public async Task<CreateWorkOrderOutcome> CreateAsync(CreateWorkOrder command, CancellationToken cancellationToken)
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

        var title = errors.Required("title", command.Title, WorkOrder.TitleMaxLength);
        var description = errors.Optional("description", command.Description, WorkOrder.DescriptionMaxLength);
        var priority = errors.EnumValue<WorkOrderPriority>("priority", command.Priority, whenMissing: WorkOrderPriority.Normal);

        if (errors.Any)
        {
            return new CreateWorkOrderOutcome.Invalid(errors.ToDictionary());
        }

        if (!await db.Customers.AnyAsync(customer => customer.Id == command.CustomerId, cancellationToken))
        {
            return new CreateWorkOrderOutcome.CustomerNotFound(command.CustomerId);
        }

        var siteCustomerId = await db.Sites
            .Where(site => site.Id == command.SiteId)
            .Select(site => (Guid?)site.CustomerId)
            .SingleOrDefaultAsync(cancellationToken);

        if (siteCustomerId is null)
        {
            return new CreateWorkOrderOutcome.SiteNotFound(command.SiteId);
        }

        if (siteCustomerId != command.CustomerId)
        {
            errors.Add("siteId", "The site does not belong to the customer.");
            return new CreateWorkOrderOutcome.Invalid(errors.ToDictionary());
        }

        var now = StoredTime.UtcNow(clock);
        var workOrder = new WorkOrder(Guid.CreateVersion7(), command.CustomerId, command.SiteId, title!, description, priority!.Value, now);

        db.WorkOrders.Add(workOrder);
        db.AppendOperationalEvent(
            WorkOrderEvents.WorkOrderCreated,
            WorkOrderEvents.WorkOrderAggregate,
            workOrder.Id,
            now,
            new WorkOrderCreatedPayload(workOrder.Id, workOrder.CustomerId, workOrder.SiteId, workOrder.Priority));

        // One SaveChanges: the work order and its event are written in a single transaction.
        await db.SaveChangesAsync(cancellationToken);

        return new CreateWorkOrderOutcome.Created(workOrder);
    }

    public async Task<IReadOnlyList<WorkOrder>> ListAsync(CancellationToken cancellationToken) =>
        await db.WorkOrders
            .AsNoTracking()
            .OrderByDescending(workOrder => workOrder.CreatedAtUtc)
            .ThenBy(workOrder => workOrder.Id)
            .ToListAsync(cancellationToken);

    public Task<WorkOrder?> GetAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        db.WorkOrders.AsNoTracking().SingleOrDefaultAsync(workOrder => workOrder.Id == workOrderId, cancellationToken);

    public async Task<ChangeWorkOrderStatusOutcome> ChangeStatusAsync(ChangeWorkOrderStatus command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var target = errors.EnumValue<WorkOrderStatus>("status", command.Status);

        if (errors.Any)
        {
            return new ChangeWorkOrderStatusOutcome.Invalid(errors.ToDictionary());
        }

        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(w => w.Id == command.WorkOrderId, cancellationToken);
        if (workOrder is null)
        {
            return new ChangeWorkOrderStatusOutcome.NotFound(command.WorkOrderId);
        }

        var oldStatus = workOrder.Status;
        if (!workOrder.TryTransitionTo(target!.Value))
        {
            return new ChangeWorkOrderStatusOutcome.TransitionNotAllowed(oldStatus, target.Value);
        }

        db.AppendOperationalEvent(
            WorkOrderEvents.WorkOrderStatusChanged,
            WorkOrderEvents.WorkOrderAggregate,
            workOrder.Id,
            StoredTime.UtcNow(clock),
            new WorkOrderStatusChangedPayload(workOrder.Id, oldStatus, workOrder.Status));

        try
        {
            // One SaveChanges: the new status and its event are written in a single transaction.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Work orders carry a row version: a concurrent change won; neither status nor event was saved.
            return new ChangeWorkOrderStatusOutcome.ConcurrentChange(workOrder.Id);
        }

        return new ChangeWorkOrderStatusOutcome.Changed(workOrder, oldStatus);
    }
}
