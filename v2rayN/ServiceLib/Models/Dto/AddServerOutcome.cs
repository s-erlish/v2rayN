namespace ServiceLib.Models.Dto;

/// <summary>
/// What an "add servers" attempt did, and how much of it. Payload of
/// <c>AppEvents.AddServerOutcomeReported</c>. Carries no text — see <see cref="EAddOutcome"/>.
/// </summary>
/// <param name="Outcome">Which branch the add took.</param>
/// <param name="Count">Servers imported, when <paramref name="Outcome"/> is
/// <see cref="EAddOutcome.ServersImported"/>; otherwise 0.</param>
public readonly record struct AddServerOutcome(EAddOutcome Outcome, int Count = 0);
