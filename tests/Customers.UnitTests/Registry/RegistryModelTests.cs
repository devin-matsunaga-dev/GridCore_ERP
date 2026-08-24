using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>The customers schema as EF actually builds it.</summary>
public class RegistryModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_module_owns_a_schema_of_its_own_and_names_its_tables_in_snake_case()
    {
        using var host = new CustomersTestHost();

        using var context = host.NewCustomersContext();

        var model = context.Model;

        Assert.Equal(CustomersDbContext.SchemaName, model.GetDefaultSchema());

        Assert.Equal("customers", model.FindEntityType(typeof(Customer))!.GetTableName());
        Assert.Equal("service_locations", model.FindEntityType(typeof(ServiceLocation))!.GetTableName());
    }

    [Fact]
    public async Task Two_customers_cannot_share_an_account_number()
    {
        // Failure path at the database, not in code: the unique index is what makes "one number,
        // one customer" true even when two registrations race the number generator.
        using var host = new CustomersTestHost();

        await using var context = host.NewCustomersContext();

        context.Customers.Add(Customer.Register("C-000001", "Sablan Family Residence", CustomerClass.Residential, Now));
        context.Customers.Add(Customer.Register("C-000001", "Taisacan Household", CustomerClass.Residential, Now.AddSeconds(1)));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_premises_cannot_share_a_location_code()
    {
        using var host = new CustomersTestHost();

        await using var context = host.NewCustomersContext();

        var address = Address.Create("128 As Nieves Road", "Songsong", "Rota", "MP");

        context.ServiceLocations.Add(ServiceLocation.Register("L-000001", address, Now));
        context.ServiceLocations.Add(ServiceLocation.Register("L-000001", address, Now.AddSeconds(1)));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_deposit_survives_the_round_trip_to_the_cent()
    {
        // Money is decimal all the way down. A float column would return 1234.5599999999999 here,
        // and the difference would first be noticed in a trial balance.
        using var host = new CustomersTestHost();

        await using (var write = host.NewCustomersContext())
        {
            write.Customers.Add(Customer.Register(
                "C-000001",
                "Garapan Beachfront Hotel",
                CustomerClass.Commercial,
                Now,
                depositHeld: 2_500.55m));

            await write.SaveChangesAsync();
        }

        await using var read = host.NewCustomersContext();

        Assert.Equal(2_500.55m, (await read.Customers.SingleAsync()).DepositHeld);
    }

    [Fact]
    public async Task An_address_is_stored_in_the_premise_row_rather_than_a_table_of_its_own()
    {
        using var host = new CustomersTestHost();

        await using (var write = host.NewCustomersContext())
        {
            write.ServiceLocations.Add(ServiceLocation.Register(
                "L-000001",
                Address.Create("22 Beach Road", "Garapan", "Saipan", "MP", "Unit 4", "96950"),
                Now));

            await write.SaveChangesAsync();
        }

        await using var read = host.NewCustomersContext();

        var location = await read.ServiceLocations.SingleAsync();

        Assert.Equal("Unit 4", location.Address.Line2);
        Assert.Equal("96950", location.Address.PostalCode);
        Assert.Equal("Saipan", location.Address.Region);
    }

    [Fact]
    public async Task A_status_is_stored_by_name_so_it_survives_a_reordered_enum()
    {
        using var host = new CustomersTestHost();

        await using (var write = host.NewCustomersContext())
        {
            write.Customers.Add(Customer.Register(
                "C-000001",
                "Sablan Family Residence",
                CustomerClass.Residential,
                Now,
                status: CustomerStatus.Active));

            await write.SaveChangesAsync();
        }

        await using var read = host.NewCustomersContext();

        var stored = await read.Database
            .SqlQuery<string>($"""select status as "Value" from customers where account_number = 'C-000001'""")
            .SingleAsync();

        Assert.Equal(nameof(CustomerStatus.Active), stored);
    }
}
