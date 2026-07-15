using AeroDesk.Core.Operations;
using Xunit;

namespace AeroDesk.Core.Tests;

/// <summary>
/// The offline departure-control demo drives the whole DCS loop in memory:
/// departures → status → check-in → board → depart, plus the transition guards.
/// </summary>
public class InMemoryOperationsServiceTests
{
    private static async Task<InMemoryOperationsService> ConnectedAsync()
    {
        var svc = new InMemoryOperationsService();
        await svc.ConnectAsync();
        return svc;
    }

    [Fact]
    public async Task Seeds_departures_for_today_at_the_default_station()
    {
        var svc = await ConnectedAsync();
        var departures = await svc.ListDeparturesAsync("DXB", DateOnly.FromDateTime(DateTime.Today));
        Assert.Equal(3, departures.Count);
        Assert.All(departures, f => Assert.Equal("DXB", f.DepartureStation));
        Assert.All(departures, f => Assert.Equal(FlightOpStatus.Scheduled, f.Status));
    }

    [Fact]
    public async Task Full_loop_start_boarding_check_in_board_depart()
    {
        var svc = await ConnectedAsync();
        var flight = (await svc.ListDeparturesAsync("DXB", DateOnly.FromDateTime(DateTime.Today)))[0];

        var boarding = await svc.ChangeStatusAsync(flight.Id, FlightOpAction.StartBoarding);
        Assert.Equal(FlightOpStatus.Boarding, boarding.Status);

        var manifest = await svc.GetManifestAsync(flight.Id);
        Assert.NotEmpty(manifest);

        var first = manifest[0];
        var checkedIn = await svc.CheckInAsync(flight.Id, first.PassengerId, 12, "C");
        Assert.Equal(PaxOpStatus.CheckedIn, checkedIn.Status);
        Assert.Equal("12C", checkedIn.Seat);

        var boarded = await svc.BoardAsync(flight.Id, first.PassengerId);
        Assert.Equal(PaxOpStatus.Boarded, boarded.Status);
        Assert.Equal(1, boarded.BoardingSequence);

        var departed = await svc.ChangeStatusAsync(flight.Id, FlightOpAction.Depart);
        Assert.Equal(FlightOpStatus.Departed, departed.Status);
    }

    [Fact]
    public async Task Board_all_boards_only_checked_in_passengers()
    {
        var svc = await ConnectedAsync();
        var flight = (await svc.ListDeparturesAsync("DXB", DateOnly.FromDateTime(DateTime.Today)))[0];
        var manifest = await svc.GetManifestAsync(flight.Id);

        // Check in two of the (>=2) passengers.
        await svc.CheckInAsync(flight.Id, manifest[0].PassengerId, null, null);
        await svc.CheckInAsync(flight.Id, manifest[1].PassengerId, null, null);

        var boarded = await svc.BoardAllAsync(flight.Id);
        Assert.Equal(2, boarded);

        var after = await svc.GetManifestAsync(flight.Id);
        Assert.Equal(2, after.Count(p => p.Status == PaxOpStatus.Boarded));
    }

    [Fact]
    public async Task Depart_before_boarding_is_rejected()
    {
        var svc = await ConnectedAsync();
        var flight = (await svc.ListDeparturesAsync("DXB", DateOnly.FromDateTime(DateTime.Today)))[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ChangeStatusAsync(flight.Id, FlightOpAction.Depart));
    }

    [Fact]
    public async Task Board_before_check_in_is_rejected()
    {
        var svc = await ConnectedAsync();
        var flight = (await svc.ListDeparturesAsync("DXB", DateOnly.FromDateTime(DateTime.Today)))[0];
        var pax = (await svc.GetManifestAsync(flight.Id))[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BoardAsync(flight.Id, pax.PassengerId));
    }
}
