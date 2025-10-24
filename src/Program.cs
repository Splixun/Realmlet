namespace Realmlet
{
    public class Program
    {
        public static void AppConfiguration(RayECS.App app)
        {

        }

        public static int Main()
        {
            return RayECS.App.Run(AppConfiguration);
        }
    }
}
