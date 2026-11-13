using Amberkeep.Storage.Config;
using Amberkeep.Storage.Targets;

namespace Amberkeep.Tests.Storage;

public sealed class TargetTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Split_uses_the_longest_volume_root()
    {
        var media = Path.Join(_fx.BasePath, "media");
        var usb = Path.Join(media, "usb");

        var (root, relative) = TargetPaths.Split(Path.Join(usb, "Backup", "Photos"), [media, usb]);

        Assert.Equal(usb + Path.DirectorySeparatorChar, root);
        Assert.Equal(Path.Join("Backup", "Photos"), relative);
    }

    [Fact]
    public void Split_does_not_match_a_root_that_is_only_a_name_prefix()
    {
        var usb = Path.Join(_fx.BasePath, "usb");

        var (_, relative) = TargetPaths.Split(Path.Join(_fx.BasePath, "usb2", "x"), [usb]);

        Assert.NotEqual("x", relative);
    }

    [Fact]
    public void Split_of_a_volume_root_has_an_empty_relative_path()
    {
        Assert.Equal(string.Empty, TargetPaths.Split(_fx.VolumeA, [_fx.VolumeA]).RelativePath);
    }

    [Fact]
    public void Adding_a_target_writes_its_marker()
    {
        var (job, target) = _fx.JobWithTarget();

        Assert.Equal(Path.Combine(_fx.VolumeA, "Backup", "Photos"), target.LastKnownPath);
        Assert.Equal(Path.Join("Backup", "Photos"), target.RelativePath);
        var marker = File.ReadAllText(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json"));
        Assert.Contains(target.Id.ToString(), marker, StringComparison.Ordinal);
        Assert.Contains(job.Id.ToString(), marker, StringComparison.Ordinal);
        Assert.Equal(TargetState.Connected, _fx.Locator.Locate(target).State);
    }

    [Fact]
    public void Target_inside_the_source_cannot_be_added()
    {
        var job = _fx.NewJob();

        Assert.Throws<InvalidOperationException>(() => _fx.Registry.AddTarget(_fx.Config, job, _fx.Source.FullPath("backup"), "Inside"));
    }

    [Fact]
    public void Folder_used_by_another_target_needs_confirmation()
    {
        var (_, target) = _fx.JobWithTarget();
        var other = _fx.NewJob("Other");

        Assert.Throws<InvalidOperationException>(() => _fx.Registry.AddTarget(new AppConfig(), other, target.LastKnownPath, "Same folder"));
        var takenOver = _fx.Registry.AddTarget(new AppConfig(), other, target.LastKnownPath, "Same folder", replaceForeignMarker: true);
        Assert.Equal(TargetState.Unverified, _fx.Locator.Locate(target).State);
        Assert.Equal(TargetState.Connected, _fx.Locator.Locate(takenOver).State);
    }

    [Fact]
    public void Target_moved_to_another_volume_is_found_by_its_marker()
    {
        var (_, target) = _fx.JobWithTarget();
        Directory.Move(Path.Combine(_fx.VolumeA, "Backup"), Path.Combine(_fx.VolumeB, "Backup"));

        var status = _fx.Locator.Locate(target);

        Assert.Equal(TargetState.Connected, status.State);
        Assert.Equal(Path.Combine(_fx.VolumeB, "Backup", "Photos"), status.Path);
    }

    [Fact]
    public void Missing_volume_means_not_connected()
    {
        var (_, target) = _fx.JobWithTarget();
        Directory.Delete(Path.Combine(_fx.VolumeA, "Backup"), recursive: true);

        Assert.Equal(TargetStatus.NotConnected, _fx.Locator.Locate(target));
    }

    [Fact]
    public void Folder_without_marker_is_unverified()
    {
        var (_, target) = _fx.JobWithTarget();
        File.Delete(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json"));

        var status = _fx.Locator.Locate(target);

        Assert.Equal(TargetState.Unverified, status.State);
        Assert.Contains("no Amberkeep marker", status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_with_a_damaged_marker_is_unverified()
    {
        var (_, target) = _fx.JobWithTarget();
        File.WriteAllText(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json"), "garbage");

        Assert.Equal(TargetState.Unverified, _fx.Locator.Locate(target).State);
    }

    [Fact]
    public void Relink_to_a_marked_folder_updates_the_location()
    {
        var (job, target) = _fx.JobWithTarget();
        var moved = Path.Combine(_fx.VolumeB, "Other place");
        Directory.Move(target.LastKnownPath, moved);

        var result = _fx.Registry.Relink(_fx.Config, job, target, moved, confirmed: false);

        Assert.Null(result.ConfirmationNeeded);
        Assert.Equal(moved, result.Target!.LastKnownPath);
        Assert.Equal("Other place", result.Target.RelativePath);
    }

    [Fact]
    public void Relink_to_an_unmarked_folder_writes_a_marker_only_when_confirmed()
    {
        var (job, target) = _fx.JobWithTarget();
        var folder = _fx.Create("volB/Old backup");

        var unconfirmed = _fx.Registry.Relink(_fx.Config, job, target, folder, confirmed: false);

        Assert.Null(unconfirmed.Target);
        Assert.NotNull(unconfirmed.ConfirmationNeeded);
        Assert.False(Directory.Exists(Path.Combine(folder, ".amberkeep")));

        var confirmed = _fx.Registry.Relink(_fx.Config, job, target, folder, confirmed: true);

        Assert.Equal(folder, confirmed.Target!.LastKnownPath);
        Assert.Equal(TargetState.Connected, _fx.Locator.Locate(confirmed.Target).State);
    }
}
