using GridCore.Modules.Inventory;
using GridCore.Platform.Modules;

namespace GridCore.Modules.Inventory.UnitTests;

public class InventoryModuleTests
{
    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new InventoryModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }
}
