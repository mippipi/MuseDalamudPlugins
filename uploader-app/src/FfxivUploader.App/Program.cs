namespace FfxivUploader.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 自己更新の補助モード（UI を起動しない）
        if (TrayContext.HandleUpdateArgs(args)) return;

        using var mutex = new Mutex(true, "FfxivStatusUploaderApp.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("FFXIV Status Uploader はすでに起動しています。", "FFXIV Status Uploader");
            return;
        }
        Application.Run(new TrayContext());
    }
}
