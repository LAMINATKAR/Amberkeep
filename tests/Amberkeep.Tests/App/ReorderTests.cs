using Amberkeep.App.Views;
using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.App;

/// <summary>Changing the order of backups on the dashboard by dragging their cards.</summary>
public sealed class ReorderTests : IDisposable
{
    private readonly AppFixture _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Job_order_follows_the_given_ids_and_keeps_unlisted_jobs_after_them()
    {
        var a = new JobConfig { Name = "A" };
        var b = new JobConfig { Name = "B" };
        var c = new JobConfig { Name = "C" };
        var d = new JobConfig { Name = "D" };
        var config = new AppConfig { Jobs = [a, b, c, d] };

        var ordered = config.WithJobOrder([c.Id, Guid.NewGuid(), a.Id, c.Id]);

        Assert.Equal(["C", "A", "B", "D"], ordered.Jobs.Select(j => j.Name));
        Assert.Equal(["A", "B", "C", "D"], config.Jobs.Select(j => j.Name));
    }

    [Fact]
    public async Task Moving_a_card_and_committing_saves_the_order()
    {
        _app.SavedJob("Photos");
        _app.SavedJob("Documents");
        _app.SavedJob("Music");
        var dashboard = _app.Shell.Dashboard;
        await dashboard.RefreshAsync();

        dashboard.MoveJob(dashboard.Jobs[2], 0);
        Assert.Equal(["Music", "Photos", "Documents"], dashboard.Jobs.Select(j => j.Name));
        Assert.Equal(["Photos", "Documents", "Music"], _app.Services.Config.Jobs.Select(j => j.Name));

        dashboard.CommitOrder();

        Assert.Equal(["Music", "Photos", "Documents"], _app.Services.Config.Jobs.Select(j => j.Name));
        Assert.Equal(["Music", "Photos", "Documents"], _app.Services.Store.Load().Config!.Jobs.Select(j => j.Name));
        await dashboard.RefreshAsync();
        Assert.Equal(["Music", "Photos", "Documents"], dashboard.Jobs.Select(j => j.Name));
    }

    [Fact]
    public async Task Cancelling_puts_the_cards_back_and_saves_nothing()
    {
        _app.SavedJob("Photos");
        _app.SavedJob("Documents");
        _app.SavedJob("Music");
        var dashboard = _app.Shell.Dashboard;
        await dashboard.RefreshAsync();

        dashboard.MoveJob(dashboard.Jobs[0], 1);
        dashboard.MoveJob(dashboard.Jobs[1], 2);
        Assert.Equal(["Documents", "Music", "Photos"], dashboard.Jobs.Select(j => j.Name));

        dashboard.CancelReorder();

        Assert.Equal(["Photos", "Documents", "Music"], dashboard.Jobs.Select(j => j.Name));
        Assert.Equal(["Photos", "Documents", "Music"], _app.Services.Config.Jobs.Select(j => j.Name));
    }

    [Fact]
    public async Task Committing_keeps_a_job_added_in_the_meantime()
    {
        _app.SavedJob("Photos");
        _app.SavedJob("Documents");
        var dashboard = _app.Shell.Dashboard;
        await dashboard.RefreshAsync();

        dashboard.MoveJob(dashboard.Jobs[1], 0);
        _app.SavedJob("Imported");
        dashboard.CommitOrder();

        Assert.Equal(["Documents", "Photos", "Imported"], _app.Services.Config.Jobs.Select(j => j.Name));
    }

    [Theory]
    [InlineData(0, 10, 0)] // still on itself
    [InlineData(0, 160, 0)] // not yet past the middle of the next card
    [InlineData(0, 180, 1)] // past the middle of the next card
    [InlineData(0, 390, 2)] // past two cards at once
    [InlineData(2, 170, 2)]
    [InlineData(2, 160, 1)]
    [InlineData(2, 10, 0)]
    public void A_dragged_card_passes_a_neighbour_at_its_middle(int current, double y, int expected)
    {
        // Three cards of 100 DIP with 16 DIP gaps: 0–100, 116–216, 232–332.
        (double, double)[] cards = [(0, 100), (116, 216), (232, 332)];

        Assert.Equal(expected, CardReorder.TargetIndex(cards, current, y));
    }
}
