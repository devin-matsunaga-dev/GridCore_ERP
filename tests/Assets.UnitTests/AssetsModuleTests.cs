using GridCore.Modules.Assets;
using GridCore.Platform.Modules;

namespace GridCore.Modules.Assets.UnitTests;

public class AssetsModuleTests
{
    [Fact]
    public void Module_declares_a_snake_case_schema_name()
    {
        IModule module = new AssetsModule();

        Assert.False(string.IsNullOrWhiteSpace(module.Name));
        Assert.Matches("^[a-z][a-z0-9_]*$", module.Name);
    }
}
