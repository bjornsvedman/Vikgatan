using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using InvoiceManager.Api.Endpoints;
using InvoiceManager.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;

namespace InvoiceManager.Api.Tests;

public sealed class ProjectTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private InvoiceDbContext _db = null!;
    private IDbContextTransaction _transaction = null!;
    private static Guid PropertyId => Property.DefaultPropertyId;
    private DbContextOptions<InvoiceDbContext> Options => new DbContextOptionsBuilder<InvoiceDbContext>()
        .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector()).Options;

    public async Task InitializeAsync()
    {
        _db = new(Options);
        _transaction = await _db.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Projects_TrimScopeRenameAndRejectDuplicateNames()
    {
        var service = new ProjectService(_db);
        var kitchen = (await service.SaveAsync(PropertyId, null, " Kitchen ", default))!;
        Assert.Equal("Kitchen", kitchen.Name);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.SaveAsync(PropertyId, null, "kITCHEN", default));
        _db.ChangeTracker.Clear();
        var other = new Property { Name = "Synthetic other property" };
        _db.Properties.Add(other);
        await _db.SaveChangesAsync();
        Assert.NotNull(await service.SaveAsync(other.Id, null, "kitchen", default));
        Assert.Null(await service.SaveAsync(other.Id, kitchen.Id, "Foreign rename", default));
        Assert.Equal("Kitchen", Assert.Single((await service.ListAsync(PropertyId, default))!).Name);
        Assert.Equal("Renamed", (await service.SaveAsync(PropertyId, kitchen.Id, " Renamed ", default))!.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(PropertyId, null, " ", default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(PropertyId, null, new string('x', 129), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(Guid.Empty, null, "Kitchen", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDuplicateCreateOrRename_HasOneWinner(bool rename)
    {
        var name = $"Synthetic race {Guid.NewGuid():N}";
        Guid? firstId = null, secondId = null;
        if (rename)
        {
            await using var setup = new InvoiceDbContext(Options);
            firstId = (await new ProjectService(setup).SaveAsync(PropertyId, null, name + " first", default))!.Id;
            secondId = (await new ProjectService(setup).SaveAsync(PropertyId, null, name + " second", default))!.Id;
        }
        async Task<bool> Save(string value, Guid? id)
        {
            await using var db = new InvoiceDbContext(Options);
            try { await new ProjectService(db).SaveAsync(PropertyId, id, value, default); return true; }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" }) { return false; }
        }
        try
        {
            var results = await Task.WhenAll(Save(name, firstId), Save(name.ToUpperInvariant(), secondId));
            Assert.Single(results, value => value);
            Assert.Single(results, value => !value);
        }
        finally
        {
            await using var cleanup = new InvoiceDbContext(Options);
            await cleanup.Projects.Where(project => project.PropertyId == PropertyId &&
                (project.NormalizedName == name.ToUpperInvariant() || project.Id == firstId || project.Id == secondId)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Review_ProjectOnlyEditsPreserveIdentityConfirmationsAndEmbedding_OmissionAndClearAreDistinct()
    {
        var project = await Project("Kitchen");
        var receipt = await Receipt(100);
        AddItem(receipt, 100, null);
        receipt.Embedding = new Vector(new float[1536]);
        receipt.EmbeddingModel = "synthetic";
        receipt.ImprovementExpenses.Add(new()
        {
            PropertyId = PropertyId, ReceiptId = receipt.Id, Description = "Synthetic work", Amount = 100,
            Currency = "SEK", Type = ImprovementType.BasicImprovement, ExpenseDate = receipt.DocumentDate,
            WorkDetails = "New synthetic installation", ConfirmedAtUtc = DateTimeOffset.UtcNow, ConfirmedReceiptVersion = 0
        });
        _db.ImprovementExpenses.AddRange(receipt.ImprovementExpenses);
        await _db.SaveChangesAsync();
        var line = Assert.Single(receipt.LineItems);
        var confirmed = Assert.Single(receipt.ImprovementExpenses).ConfirmedAtUtc;
        var review = new ReceiptReviewService(_db);
        var result = await review.ReviewAsync(receipt.Id, Review(receipt, Line(line) with { ProjectId = project.Id }), default);
        Assert.Equal(ReceiptReviewStatus.Updated, result.Status);
        Assert.Equal(line.Id, Assert.Single(result.Receipt!.LineItems).Id);
        Assert.Equal("Kitchen", result.Receipt.LineItems[0].ProjectName);
        Assert.Equal(confirmed, result.Receipt.Expenses[0].ConfirmedAtUtc);
        Assert.Equal(receipt.ReviewVersion, result.Receipt.Expenses[0].ConfirmedReceiptVersion);
        Assert.NotNull(receipt.Embedding);
        var omitted = JsonSerializer.Deserialize<UpdateReceiptLineItemRequest>(
            $$"""{"id":"{{line.Id}}","description":"Synthetic item","lineTotal":100}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(omitted.ProjectAssignmentSupplied);
        result = await review.ReviewAsync(receipt.Id, Review(receipt, omitted), default);
        Assert.Equal(project.Id, result.Receipt!.LineItems[0].ProjectId);
        var explicitlyNull = JsonSerializer.Deserialize<UpdateReceiptLineItemRequest>(
            $$"""{"id":"{{line.Id}}","description":"Synthetic item","lineTotal":100,"projectId":null}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(explicitlyNull.ProjectAssignmentSupplied);
        result = await review.ReviewAsync(receipt.Id, Review(receipt, explicitlyNull), default);
        Assert.Null(result.Receipt!.LineItems[0].ProjectId);
        Assert.Equal(confirmed, result.Receipt.Expenses[0].ConfirmedAtUtc);
        Assert.NotNull(receipt.Embedding);
        result = await review.ReviewAsync(receipt.Id, Review(receipt, Line(line) with { LineTotal = 99 }), default);
        Assert.Equal(line.Id, result.Receipt!.LineItems[0].Id);
        Assert.Null(result.Receipt.Expenses[0].ConfirmedAtUtc);
        Assert.Null(receipt.Embedding);
    }

    [Fact]
    public async Task Review_RejectsForeignDuplicateLegacyStaleAndMixedAssignments()
    {
        var kitchen = await Project("Kitchen");
        var bath = await Project("Bathroom");
        var other = new Property { Name = "Other" };
        _db.Properties.Add(other);
        await _db.SaveChangesAsync();
        var foreign = (await new ProjectService(_db).SaveAsync(other.Id, null, "Kitchen", default))!;
        var receipt = await Receipt(1000);
        AddItem(receipt, 600, kitchen.Id);
        AddItem(receipt, 400, kitchen.Id);
        await _db.SaveChangesAsync();
        var lines = receipt.LineItems.Select(Line).ToArray();
        var service = new ReceiptReviewService(_db);
        foreach (var request in new[]
        {
            Review(receipt, lines[0] with { ProjectId = foreign.Id }, lines[1]),
            Review(receipt, lines[0], lines[0]),
            Review(receipt, lines[0] with { Id = Guid.NewGuid() }, lines[1]),
            Review(receipt, lines[0] with { Id = null }, lines[1] with { Id = null }),
            Review(receipt, lines) with { ReviewVersion = null }
        })
            Assert.Equal(ReceiptReviewStatus.Invalid, (await service.ReviewAsync(receipt.Id, request, default)).Status);
        Assert.Equal(ReceiptReviewStatus.Conflict, (await service.ReviewAsync(receipt.Id,
            Review(receipt, lines) with { ReviewVersion = -1 }, default)).Status);
        var mixed = await service.ReviewAsync(receipt.Id, Review(receipt, lines[0], lines[1] with { ProjectId = bath.Id }), default);
        Assert.Equal(ReceiptReviewStatus.Invalid, mixed.Status);
        Assert.Contains("All line items on a receipt must belong to the same project.", mixed.ValidationErrors);
        var unified = await service.ReviewAsync(receipt.Id,
            Review(receipt, lines[0] with { ProjectId = bath.Id }, lines[1] with { ProjectId = bath.Id }), default);
        Assert.All(unified.Receipt!.LineItems, item => Assert.Equal(bath.Id, item.ProjectId));
        var removed = await service.ReviewAsync(receipt.Id, Review(receipt, lines[0]), default);
        Assert.Single(removed.Receipt!.LineItems);
        Assert.Equal(lines[0].Id, removed.Receipt.LineItems[0].Id);
    }

    [Fact]
    public async Task Database_RejectsCrossPropertyAssignment_AndReceiptDeletionPreservesProjects()
    {
        var other = new Property { Name = "Other" };
        _db.Properties.Add(other);
        await _db.SaveChangesAsync();
        var foreign = (await new ProjectService(_db).SaveAsync(other.Id, null, "Foreign", default))!;
        var receipt = await Receipt(100);
        AddItem(receipt, 100, null);
        await _db.SaveChangesAsync();
        var line = receipt.LineItems[0];
        await _transaction.CreateSavepointAsync("foreign_project");
        var error = await Assert.ThrowsAsync<PostgresException>(() => _db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "ReceiptLineItems" SET "ProjectId" = {foreign.Id} WHERE "Id" = {line.Id}"""));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        await _transaction.RollbackToSavepointAsync("foreign_project");
        var kitchen = await Project("Kitchen");
        line.ProjectId = kitchen.Id;
        await _db.SaveChangesAsync();
        await _db.Receipts.Where(item => item.Id == receipt.Id).ExecuteDeleteAsync();
        Assert.False(await _db.ReceiptLineItems.AnyAsync(item => item.Id == line.Id));
        Assert.True(await _db.Projects.AnyAsync(project => project.Id == kitchen.Id));
    }

    [Fact]
    public async Task ArchiveAndReport_FilterOnce_ShowAllProjects_AndNeverAllocateClassifiedAmounts()
    {
        var kitchen = await Project("<b>Kitchen</b>");
        var bath = await Project("Bathroom");
        var empty = await Project("Empty");
        var receipt = await Receipt(1000);
        AddItem(receipt, 600, kitchen.Id);
        AddItem(receipt, 200, kitchen.Id);
        AddItem(receipt, 200, bath.Id);
        receipt.ImprovementExpenses.Add(new()
        {
            PropertyId = PropertyId, ReceiptId = receipt.Id, Description = "Work", Type = ImprovementType.BasicImprovement,
            ExpenseDate = receipt.DocumentDate, Amount = 1000, Currency = "SEK", WorkDetails = "New work",
            ConfirmedAtUtc = DateTimeOffset.UtcNow, ConfirmedReceiptVersion = receipt.ReviewVersion
        });
        _db.ImprovementExpenses.AddRange(receipt.ImprovementExpenses);
        var noItems = await Receipt(50);
        var partial = await Receipt(20);
        AddItem(partial, 20, null);
        await _db.SaveChangesAsync();
        var archive = new ReceiptArchiveService(_db);
        var page = await archive.BrowseAsync(new(PropertyId, ProjectId: kitchen.Id, PageSize: 1), default);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(receipt.Id, Assert.Single(page.Items).Id);
        Assert.Contains(page.Projects, project => project.Id == empty.Id);
        Assert.Contains(page.Items[0].LineItems, item => item.ProjectName == "Bathroom");
        Assert.Equal(0, (await archive.BrowseAsync(new(PropertyId, ProjectId: empty.Id), default)).TotalCount);
        Assert.Equal(new[] { noItems.Id, partial.Id }.Order(), (await archive.BrowseAsync(new(PropertyId, UnassignedProject: true), default)).Items.Select(item => item.Id).Order());
        await Assert.ThrowsAsync<ArgumentException>(() => archive.BrowseAsync(new(PropertyId, ProjectId: kitchen.Id, UnassignedProject: true), default));
        await Assert.ThrowsAsync<ArgumentException>(() => archive.BrowseAsync(new(PropertyId, ProjectId: Guid.NewGuid()), default));
        var report = (await new ExpenseReportService(_db).GenerateAsync(PropertyId, default, kitchen.Id))!;
        Assert.Equal(1, report.ReceiptCount);
        Assert.Equal("1000.00", report.IncludedTotal);
        Assert.Contains("not project-only spending", report.ScopeNotice);
        Assert.Equal(new[] { "<b>Kitchen</b>", "Bathroom" }, Assert.Single(report.Included).SourceProjects);
        Assert.Contains("&lt;b&gt;Kitchen&lt;/b&gt;", report.Html);
        Assert.DoesNotContain("<b>Kitchen</b>", report.Html);
        Assert.Single((await new ImprovementService(_db).ListAsync(PropertyId, default, kitchen.Id))!);
        await new ProjectService(_db).SaveAsync(PropertyId, kitchen.Id, "Renamed", default);
        report = (await new ExpenseReportService(_db).GenerateAsync(PropertyId, default, kitchen.Id))!;
        Assert.Contains("Renamed", report.Included[0].SourceProjects);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("project")]
    [InlineData("year")]
    [InlineData("vendor")]
    public async Task Spending_UsesExactMatchingItemAmountsAndDisclosesCoverage(string groupBy)
    {
        var kitchen = await Project("Kitchen");
        var bath = await Project("Bathroom");
        var receipt = await Receipt(1000);
        AddItem(receipt, 600.1234m, kitchen.Id);
        AddItem(receipt, 349.8766m, bath.Id);
        var missing = await Receipt(10);
        AddItem(missing, null, kitchen.Id);
        var unassigned = await Receipt(2);
        AddItem(unassigned, 2, null);
        await Receipt(50);
        var foreignProperty = new Property { Name = "Other property" };
        _db.Properties.Add(foreignProperty);
        await Receipt(800, foreignProperty.Id);
        var pending = await Receipt(900);
        pending.ProcessingStatus = ReceiptProcessingStatus.NeedsReview;
        AddItem(pending, 900, kitchen.Id);
        await _db.SaveChangesAsync();
        var planner = new Planner { Plan = new("sum_total", 2024, "synthetic vendor", groupBy) };
        var provider = new AnswerProvider();
        var service = Questions(planner, provider);
        var result = await service.QueryAsync("How much spent?", default, PropertyId, kitchen.Id);
        var calculation = result.Calculation!;
        Assert.True(calculation.ItemBased);
        Assert.Equal("600.1234", Assert.Single(calculation.Totals).Amount);
        Assert.Equal(2, calculation.Items.Count);
        Assert.Contains(calculation.Items, item => item.Amount == null);
        Assert.DoesNotContain(calculation.Items, item => item.ProjectId == bath.Id);
        Assert.Contains(calculation.CoverageWarnings, message => message.Contains("missing amounts"));
        Assert.Contains(calculation.CoverageWarnings, message => message.Contains("50.00 SEK"));
        Assert.Contains(calculation.CoverageWarnings, message => message.Contains("no items"));
        Assert.Equal(2, result.TotalSourceCount);
        Assert.Null(provider.Documents);
        if (groupBy == "project")
        {
            var all = await service.QueryAsync("Totals by project", default, PropertyId);
            Assert.Equal(3, all.Calculation!.Totals.Count);
            Assert.Equal("349.8766", Assert.Single(all.Calculation.Totals, total => total.Label == "Bathroom").Amount);
            Assert.Equal("2.00", Assert.Single(all.Calculation.Totals, total => total.Label == "Unassigned").Amount);
        }
    }

    [Fact]
    public async Task NamedScope_RejectsUnknownConflictingOrForeignScope_AndSemanticContextIsFilteredBeforeLimit()
    {
        var kitchen = await Project("Kitchen");
        var bath = await Project("Bathroom");
        var matching = await Receipt(100);
        AddItem(matching, 60, kitchen.Id);
        AddItem(matching, 40, bath.Id);
        for (var index = 0; index < 12; index++)
        {
            var newer = await Receipt(2);
            AddItem(newer, 2, bath.Id);
        }
        await _db.SaveChangesAsync();
        var planner = new Planner { Plan = new("semantic", null, null, "none", " kitchen ") };
        var provider = new AnswerProvider();
        var service = Questions(planner, provider);
        var result = await service.QueryAsync("Show receipts for project Kitchen", default, PropertyId);
        Assert.Equal(matching.Id, Assert.Single(provider.Documents!).Id);
        Assert.Single(provider.Documents![0].LineItems);
        Assert.Equal(kitchen.Id, Assert.Single(Assert.Single(result.Sources).MatchedItems).ProjectId);
        Assert.Equal(new[] { "Bathroom", "Kitchen" }, result.Sources[0].SourceProjects);
        Assert.Contains("other projects", result.Answer);
        Assert.Contains("conflicts", (await service.QueryAsync("Show Kitchen", default, PropertyId, bath.Id)).Answer);
        Assert.Contains("conflicts", (await service.QueryAsync("Show Kitchen", default, PropertyId, null, true)).Answer);
        planner.Plan = planner.Plan with { Project = "Missing" };
        Assert.Contains("No project exactly matches", (await service.QueryAsync("Show Missing", default, PropertyId)).Answer);
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync("Show", default, PropertyId, Guid.NewGuid()));
    }

    [Fact]
    public async Task ItemTotals_KeepCurrenciesAndUnassignedIdentitySeparate_AndMissingOnlyIsNotNoSpending()
    {
        var kitchen = await Project("Kitchen");
        var namedUnassigned = await Project("Unassigned");
        var sek = await Receipt(30);
        AddItem(sek, 10, namedUnassigned.Id);
        AddItem(sek, 20, null);
        var euro = await Receipt(5);
        euro.Currency = "EUR";
        AddItem(euro, 5, kitchen.Id);
        var missing = await Receipt(99);
        AddItem(missing, null, kitchen.Id);
        await _db.SaveChangesAsync();
        var planner = new Planner { Plan = new("sum_total", null, null, "project") };
        var service = Questions(planner, new());
        var grouped = (await service.QueryAsync("Totals by project", default, PropertyId)).Calculation!;
        Assert.Equal("10.00", Assert.Single(grouped.Totals, total => total.ProjectId == namedUnassigned.Id).Amount);
        Assert.Equal("20.00", Assert.Single(grouped.Totals, total => total.UnassignedProject).Amount);
        Assert.Equal("5.00", Assert.Single(grouped.Totals, total => total.ProjectId == kitchen.Id && total.Currency == "EUR").Amount);
        Assert.Equal("0.00", Assert.Single(grouped.Totals, total => total.ProjectId == kitchen.Id && total.Currency == "SEK").Amount);
        Assert.Contains(grouped.CoverageWarnings, message => message.Contains("zero known sum"));
        planner.Plan = planner.Plan with { Project = "Kitchen" };
        var other = new Property { Name = "Other property" };
        _db.Properties.Add(other);
        await _db.SaveChangesAsync();
        var foreign = (await new ProjectService(_db).SaveAsync(other.Id, null, "Kitchen", default))!;
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync("Totals", default, PropertyId, foreign.Id));
        var local = await service.QueryAsync("Kitchen spending", default, PropertyId);
        Assert.All(local.Calculation!.Items, item => Assert.Equal(kitchen.Id, item.ProjectId));
        Assert.All(local.Sources, source => Assert.NotEqual(foreign.Id, source.MatchedItems.FirstOrDefault()?.ProjectId));
    }

    [Fact]
    public async Task ProjectEndpoints_ExposeValidationConflictAndScopeOverHttp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_db);
        builder.Services.AddScoped<ProjectService>();
        await using var app = builder.Build();
        app.MapProjectEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)) };
            var route = $"/api/properties/{PropertyId}/projects/";
            using var created = await client.PostAsJsonAsync(route, new { name = " Kitchen " });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var project = (await created.Content.ReadFromJsonAsync<ProjectResponse>())!;
            using var conflict = await client.PostAsJsonAsync(route, new { name = "KITCHEN" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            _db.ChangeTracker.Clear();
            using var invalid = await client.PostAsJsonAsync(route, new { name = "" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var empty = await client.GetAsync($"/api/properties/{Guid.Empty}/projects/");
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            using var unknown = await client.PutAsJsonAsync($"/api/properties/{Guid.NewGuid()}/projects/{project.Id}", new { name = "Other" });
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            await Project("Bathroom");
            using var renameConflict = await client.PutAsJsonAsync(route + project.Id, new { name = "BATHROOM" });
            Assert.Equal(HttpStatusCode.Conflict, renameConflict.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private async Task<ProjectResponse> Project(string name) =>
        (await new ProjectService(_db).SaveAsync(PropertyId, null, name, default))!;

    private async Task<Receipt> Receipt(decimal amount, Guid? propertyId = null)
    {
        var receipt = new Receipt
        {
            PropertyId = propertyId ?? PropertyId, FileName = "synthetic-project.pdf", FilePath = "synthetic-project.pdf",
            ContentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", Vendor = "Synthetic vendor",
            DocumentDate = new(2024, 5, 1), Currency = "SEK", TotalAmount = amount,
            ProcessingStatus = ReceiptProcessingStatus.Completed
        };
        _db.Receipts.Add(receipt);
        await _db.SaveChangesAsync();
        return receipt;
    }

    private void AddItem(Receipt receipt, decimal? amount, Guid? projectId)
    {
        var item = new ReceiptLineItem
        {
            PropertyId = receipt.PropertyId, ReceiptId = receipt.Id, Description = "Synthetic item",
            LineTotal = amount, ProjectId = projectId
        };
        receipt.LineItems.Add(item);
        _db.ReceiptLineItems.Add(item);
    }

    private static UpdateReceiptLineItemRequest Line(ReceiptLineItem item) => new()
    {
        Id = item.Id, Description = item.Description, Quantity = item.Quantity, UnitPrice = item.UnitPrice,
        LineTotal = item.LineTotal
    };

    private static UpdateReceiptReviewRequest Review(Receipt receipt, params UpdateReceiptLineItemRequest[] items) => new()
    {
        Vendor = receipt.Vendor, DocumentDate = receipt.DocumentDate, Currency = receipt.Currency,
        TotalAmount = receipt.TotalAmount, Description = receipt.Description,
        ReviewVersion = receipt.ReviewVersion, LineItems = items.ToList()
    };

    private ReceiptQuestionService Questions(Planner planner, AnswerProvider provider) =>
        new(_db, new StubEmbeddingProvider(), planner, provider, NullLogger<ReceiptQuestionService>.Instance);

    private sealed class Planner : IReceiptQueryPlanner
    {
        public ReceiptQueryPlan Plan { get; set; } = new("semantic", null, null, "none");
        public Task<ReceiptQueryPlan> PlanAsync(string question, CancellationToken ct) => Task.FromResult(Plan);
    }

    private sealed class AnswerProvider : IReceiptAnswerProvider
    {
        public IReadOnlyList<ReceiptQuestionDocument>? Documents { get; private set; }
        public Task<ReceiptAnswerDraft> AnswerAsync(string question, IReadOnlyList<ReceiptQuestionDocument> documents, CancellationToken ct)
        {
            Documents = documents;
            return Task.FromResult(new ReceiptAnswerDraft("Relevant receipt found.", documents.Select(item => item.Id).ToArray()));
        }
    }
}
