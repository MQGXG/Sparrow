using System.Text;
using Lertaro.Core.Services.Search;
using Lertaro.Core.Wire;

Console.OutputEncoding = Encoding.UTF8;

var client = new SearchPipeClient();
Console.WriteLine($"ping: {await client.PingAsync()}");

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
