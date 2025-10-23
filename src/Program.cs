namespace Realmlet
{
    public class Program
    {
        public static int Main()
        {
            RayECS.App.New(app =>
            {
                app.Run();
            });
            return 0;
        }
    }
}
