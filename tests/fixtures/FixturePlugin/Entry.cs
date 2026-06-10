namespace FixturePlugin
{
    public static class Entry
    {
        // Crossing into the dependency proves the loader resolved
        // FixtureDep.dll from the plugin's build directory.
        public static string GetDepMessage() => FixtureDep.DepValue.Message;
    }
}
