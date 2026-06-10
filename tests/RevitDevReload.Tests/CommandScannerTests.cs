using System.Linq;

using RevitDevReload.Core;

using Xunit;

// CommandScanner matches IExternalCommand by interface NAME (not type
// identity) so it works across RevitAPI versions and is testable without
// Revit. These fixture types live in a namespace whose interface is named
// exactly IExternalCommand.
namespace RevitDevReload.Tests.FakeRevitApi
{
    public interface IExternalCommand { }
    public interface IExternalApplication { }

    public class GoodCommand : IExternalCommand { }
    public class AnotherCommand : IExternalCommand { }
    public abstract class AbstractCommand : IExternalCommand { }
    internal class InternalCommand : IExternalCommand { }
    public class NotACommand { }
    public class FakeApp : IExternalApplication { }
}

namespace RevitDevReload.Tests
{
    public class CommandScannerTests
    {
        [Fact]
        public void FindExternalCommands_ReturnsPublicConcreteImplementations()
        {
            var commands = CommandScanner.FindExternalCommands(
                typeof(FakeRevitApi.GoodCommand).Assembly);

            var names = commands.Select(c => c.FullClassName).ToList();
            Assert.Contains("RevitDevReload.Tests.FakeRevitApi.GoodCommand", names);
            Assert.Contains("RevitDevReload.Tests.FakeRevitApi.AnotherCommand", names);
            Assert.DoesNotContain("RevitDevReload.Tests.FakeRevitApi.AbstractCommand", names);
            Assert.DoesNotContain("RevitDevReload.Tests.FakeRevitApi.InternalCommand", names);
            Assert.DoesNotContain("RevitDevReload.Tests.FakeRevitApi.NotACommand", names);
        }

        [Fact]
        public void FindExternalCommands_ShortNameIsTypeName()
        {
            var commands = CommandScanner.FindExternalCommands(
                typeof(FakeRevitApi.GoodCommand).Assembly);

            var good = commands.Single(
                c => c.FullClassName == "RevitDevReload.Tests.FakeRevitApi.GoodCommand");
            Assert.Equal("GoodCommand", good.DisplayName);
        }

        [Fact]
        public void FindExternalApplications_FindsByInterfaceName()
        {
            var apps = CommandScanner.FindExternalApplications(
                typeof(FakeRevitApi.FakeApp).Assembly);

            Assert.Contains("RevitDevReload.Tests.FakeRevitApi.FakeApp",
                apps.Select(a => a.FullName));
        }
    }
}
