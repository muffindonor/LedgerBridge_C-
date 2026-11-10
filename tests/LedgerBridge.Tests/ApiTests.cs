using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LedgerBridge.Tests;

[Collection(PostgresCollection.Name)]
public class ApiTests(PostgresFixture postgres)
{
    private static HttpClient CreateClient(TestLedger ledger, out WebApplicationFactory<Program> factory)
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseSetting("ConnectionStrings:Ledger", ledger.ConnectionString));
        return factory.CreateClient();
    }

    [Fact]
    public async Task Account_shows_the_reconciled_balance()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));
        using HttpClient client = CreateClient(ledger, out WebApplicationFactory<Program> factory);
        await using (factory)
        {
            JsonElement account = await client.GetFromJsonAsync<JsonElement>("/accounts/1000000001");

            Assert.Equal(280000, account.GetProperty("balanceCents").GetInt64());
            Assert.Equal("ALICE COHEN", account.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Unknown_account_is_404()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        using HttpClient client = CreateClient(ledger, out WebApplicationFactory<Program> factory);
        await using (factory)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/accounts/9999999999")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/batches/12345")).StatusCode);
        }
    }

    [Fact]
    public async Task Statement_pages_through_every_entry_with_signed_amounts()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));
        using HttpClient client = CreateClient(ledger, out WebApplicationFactory<Program> factory);
        await using (factory)
        {
            var seen = new List<JsonElement>();
            string query = "/accounts/1000000001/entries?limit=2";
            while (true)
            {
                JsonElement[] page = (await client.GetFromJsonAsync<JsonElement[]>(query))!;
                if (page.Length == 0)
                {
                    break;
                }

                seen.AddRange(page);
                JsonElement last = page[^1];
                query = $"/accounts/1000000001/entries?limit=2&afterBatch={last.GetProperty("batchId").GetInt64()}&afterKey={Uri.EscapeDataString(last.GetProperty("entryKey").GetString()!)}";
            }

            Assert.Equal(3, seen.Count);
            Assert.Equal(seen.Count, seen.Select(e => e.GetProperty("entryKey").GetString()).Distinct().Count());
            Assert.Equal(280000, seen.Sum(e => e.GetProperty("signedAmountCents").GetInt64()));
            Assert.Contains(seen, e => e.GetProperty("signedAmountCents").GetInt64() < 0);
        }
    }

    [Fact]
    public async Task Rejected_batch_reports_its_breaks()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        using var bad = new TempBatch("day1");
        bad.Replace("ACCTMAST.NEW", "+0000000280000", "+0000000280001");
        await ledger.ProcessAsync(bad.Directory);
        using HttpClient client = CreateClient(ledger, out WebApplicationFactory<Program> factory);
        await using (factory)
        {
            JsonElement[] batches = (await client.GetFromJsonAsync<JsonElement[]>("/batches"))!;
            long id = Assert.Single(batches).GetProperty("id").GetInt64();
            JsonElement batch = await client.GetFromJsonAsync<JsonElement>($"/batches/{id}");

            Assert.Equal("REJECTED", batch.GetProperty("status").GetString());
            Assert.Equal("BALANCE_MISMATCH", batch.GetProperty("breaks")[0].GetProperty("check").GetString());
        }
    }
}
