namespace HSPI_RiskRewardStatus
{
    internal static class Program
    {
        private static HSPI plugin;

        public static void Main(string[] args)
        {
            plugin = new HSPI();
            plugin.Connect(args);
        }
    }
}
