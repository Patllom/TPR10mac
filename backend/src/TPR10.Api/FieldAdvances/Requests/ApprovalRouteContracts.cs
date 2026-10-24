namespace TPR10.Api.FieldAdvances.Requests;

public sealed record RouteChoice(Guid? PersonId, Guid? AccountId, string? HoldCode, Guid[] TraversedPersonIds);

internal sealed record RoutePerson(Guid? PersonId, string? HoldCode);
