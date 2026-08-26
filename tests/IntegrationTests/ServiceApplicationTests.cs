using System.Text;
using GridCore.Contracts.Providers;
using GridCore.Contracts.Services;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Applications;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Platform.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// WP-2.18 against real infrastructure: a scanned document goes into <b>real MinIO</b>, its record
/// goes into real Postgres, and an approval opens a real service account in the same transaction.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves everything that does not need a container — the checklist, the state
/// machine, the reason codes, the content-type refusal, the two permission gates — in milliseconds,
/// with a dictionary standing in for the object store (CONVENTIONS.md rule C). What only a
/// container can show is the claim the seam actually makes: that <c>MinioDocumentStore</c> and the
/// fast tier's <c>FakeDocumentStore</c> agree, that a bucket is created on a volume that has never
/// held one, and that the bytes come back byte-for-byte with the checksum the row recorded.
/// </para>
/// <para>
/// Three tests and no more. This is the gate tier: the round trip, the transaction, and the one
/// answer that is <b>not</b> an exception — a key nothing was ever filed under.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ServiceApplicationTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_object_store_behind_the_seam_is_real_MinIO_and_it_round_trips()
    {
        await using var scope = fixture.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        // Registered against the Contracts seam, and the implementation behind it is the MinIO one:
        // a gate suite that silently ran on a fake would prove nothing at all.
        Assert.IsType<MinioDocumentStore>(store);

        var key = $"{ServiceApplicationService.StoragePrefix}/{Guid.CreateVersion7():D}/round-trip.pdf";
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.7\nA scanned lease, or near enough.\n%%EOF");

        var stored = await store.PutAsync(new DocumentUpload(key, "application/pdf", bytes));

        Assert.Equal(key, stored.Key);
        Assert.Equal(bytes.Length, stored.SizeInBytes);

        var read = await store.GetAsync(key);

        Assert.NotNull(read);
        Assert.Equal(bytes, read.Content.ToArray());
        Assert.Equal("application/pdf", read.ContentType);
        Assert.Equal(bytes.Length, read.SizeInBytes);

        // The digest the register would have recorded, computed over what came back out.
        Assert.Equal(
            ServiceApplicationService.Checksum(bytes),
            ServiceApplicationService.Checksum(read.Content.Span));
    }

    [Fact]
    public async Task A_key_nothing_was_filed_under_is_an_answer_rather_than_a_failure()
    {
        // The one outcome the seam promises is not an exception. A row whose object has gone has to
        // be reportable as a missing document, not as a store that fell over.
        await using var scope = fixture.CreateScope();

        var missing = await scope.ServiceProvider.GetRequiredService<IDocumentStore>()
            .GetAsync($"{ServiceApplicationService.StoragePrefix}/{Guid.CreateVersion7():D}/never-written.pdf");

        Assert.Null(missing);
    }

    [Fact]
    public async Task An_approved_application_files_its_evidence_in_MinIO_and_opens_the_account_in_Postgres()
    {
        var (customerId, premiseId) = await AnApplicantAsync("A1");

        Guid applicationId;

        await using (var scope = fixture.CreateScope())
        {
            applicationId = (await scope.ServiceProvider.GetRequiredService<IServiceApplicationService>()
                .SubmitAsync(new SubmitApplicationInput(customerId, premiseId, ServiceType.Electricity, Notes: "Filed at the counter.")))
                .Id;
        }

        // A scope per act, so each commits on its own request — the shape a real desk produces.
        foreach (var kind in ServiceApplicationTypes.RequiredDocuments(ServiceApplicationType.ResidentialConnection))
        {
            await using var scope = fixture.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IServiceApplicationService>()
                .AttachDocumentAsync(
                    applicationId,
                    new AttachDocumentInput(kind, $"{kind}.pdf", "application/pdf", Encoding.UTF8.GetBytes($"%PDF-1.7 {kind}")));
        }

        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IServiceApplicationService>().StartReviewAsync(applicationId);
        }

        ApplicationApproval approval;

        await using (var scope = fixture.CreateScope())
        {
            approval = await scope.ServiceProvider.GetRequiredService<IServiceApplicationService>()
                .ApproveAsync(applicationId, new DecideApplicationInput(ApplicationReasonCode.DocumentsVerified, "Lease sighted."));
        }

        Assert.Equal(ServiceApplicationStatus.Approved, approval.Application.Status);
        Assert.Equal(ServiceAccountStatus.Pending, approval.Account.Status);
        Assert.Equal(approval.Account.Id, approval.Application.ServiceAccountId);
        Assert.True(approval.Deposit.RequiredAmount > 0m);

        await using var read = fixture.CreateScope();

        // The rows are in Postgres, through the migration this package added.
        var database = read.ServiceProvider.GetRequiredService<CustomersDbContext>();

        var stored = await database.ServiceApplications
            .AsNoTracking()
            .Include(application => application.Documents)
            .SingleAsync(application => application.Id == applicationId);

        Assert.Equal(2, stored.Documents.Count);
        Assert.True(stored.IsDocumentationComplete);
        Assert.Equal(approval.Account.Id, stored.ServiceAccountId);

        Assert.True(await database.ServiceAccounts.AnyAsync(account => account.Id == approval.Account.Id));

        // And the bytes are in the bucket, under the key each row recorded, hashing to what it says.
        var store = read.ServiceProvider.GetRequiredService<IDocumentStore>();

        foreach (var document in stored.Documents)
        {
            var content = await store.GetAsync(document.StorageKey);

            Assert.NotNull(content);
            Assert.Equal(document.SizeInBytes, content.SizeInBytes);
            Assert.Equal(document.Checksum, ServiceApplicationService.Checksum(content.Content.Span));
        }
    }

    /// <summary>Registers a customer and a premise with no supply taken at it — where an application starts.</summary>
    private async Task<(Guid CustomerId, Guid PremiseId)> AnApplicantAsync(string tag)
    {
        await using var scope = fixture.CreateScope();

        var customer = await scope.ServiceProvider.GetRequiredService<ICustomerService>()
            .RegisterAsync(new RegisterCustomerInput($"Application customer {tag}", CustomerClass.Residential));

        var premise = await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
            .RegisterAsync(new ServiceLocationInput(
                Address.Create($"{tag} As Nieves Road", "Songsong", "Rota", "MP", postalCode: "96951"),
                "Meter on the north wall"));

        return (customer.Id, premise.Id);
    }
}
