using System.Text;
using Lertaro.Core;
using Lertaro.Core.Services.Search;
using Lertaro.Core.Wire;

Console.OutputEncoding = Encoding.UTF8;

var client = new SearchPipeClient();
var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "search";
var ping = await client.PingAsync();
Console.WriteLine($"ping: {ping}");

if (!ping)
    return 2;

if (mode == "status")
{
    var status = await client.GetStatusAsync();
    Console.WriteLine($"state={status.State} files={status.TotalFiles} dirs={status.TotalDirs}");

    foreach (var drive in status.Drives)
    {
        Console.WriteLine(
            $"{drive.Drive}: enabled={drive.Enabled} state={drive.State} files={drive.Files} dirs={drive.Dirs}");
    }

    return 0;
}

if (mode == "set-drives")
{
    var drives = (args.Length > 1 ? args[1] : "C,D")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var ids = drives
        .Select(VolumeHelper.GetVolumeId)
        .OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var response = await client.SendPipeCommandAsync(
        new SearchRequestMessage
        {
            Id = SearchRequestId.SetMachineSettings,
            MachineSettings = new MachineSettings
            {
                LocalDrives = ids,
                LocalDriveSelectionConfigured = true
            }
        },
        CancellationToken.None);

    Console.WriteLine($"set-drives [{string.Join(",", drives)}]: {response.Kind} {response.Message}");

    foreach (var drive in drives)
    {
        var rebuild = await client.SendPipeCommandAsync(
            new SearchRequestMessage { Id = SearchRequestId.RebuildDrive, Drive = drive },
            CancellationToken.None);

        Console.WriteLine($"rebuild {drive}: {rebuild.Kind} {rebuild.Message}");
    }

    return 0;
}

if (mode == "delete-drive")
{
    var drive = args.Length > 1 ? args[1] : "D";
    var response = await client.SendPipeCommandAsync(
        new SearchRequestMessage { Id = SearchRequestId.DeleteDriveIndex, Drive = drive },
        CancellationToken.None);

    Console.WriteLine($"delete {drive}: {response.Kind} {response.Message}");
    return 0;
}

var query = args.Length > 0 ? args[0] : "sparrow-index-test";
var count = 0;

var message = new SearchRequestMessage
{
    Id = SearchRequestId.Search,
    Limit = 20,
    AppLimit = 0,
    Query = query,
    DirectoryFilter = null,
    ExactMatch = false,
    OrFirstPrecedence = false
};

await SearchPipeClient.SendSearchPipeCommandAsync(
    message,
    row =>
    {
        count++;
        Console.WriteLine($"{row.Name} | {row.Path}");
    },
    CancellationToken.None);

Console.WriteLine($"total: {count}");
return 0;
