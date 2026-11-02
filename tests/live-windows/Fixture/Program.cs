using System.Windows.Forms;
using System.Drawing;
using System.IO;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var controlPath = args[0];
        string lastCommand="";
        Form? first = Make("AppGuard LIVE TEST - Main", 300, 140);
        Form? second = null;
        var ctx = new ApplicationContext();
        first.Show();
        File.WriteAllText(controlPath+".ready", Environment.ProcessId.ToString());
        var timer = new System.Windows.Forms.Timer{ Interval=110 };
        timer.Tick += (_,_) =>
        {
            if(!File.Exists(controlPath))return;
            string v;
            try{v=File.ReadAllText(controlPath);}catch{return;}
            if(v==lastCommand)return;
            lastCommand=v;
            string cmd=v.Split('|')[0];
            try
            {
                switch(cmd)
                {
                    case "MINIMIZE": if(first!=null)first.WindowState=FormWindowState.Minimized;break;
                    case "RESTORE": if(first!=null){first.Show();first.WindowState=FormWindowState.Normal;}break;
                    case "SHOW_SECOND": second??=Make("AppGuard LIVE TEST - Second",460,260);second.Show();break;
                    case "CLOSE_SECOND": second?.Close();second=null;break;
                    case "CLOSE_MAIN": first?.Close();first=null;break;
                    case "REOPEN_MAIN": first??=Make("AppGuard LIVE TEST - Reopened",300,140);first.Show();break;
                    case "HIDE_MAIN": first?.Hide();break;
                    case "SHOW_MAIN": first?.Show();break;
                    case "EXIT":
                        timer.Stop();second?.Close();first?.Close();Application.ExitThread();break;
                }
                File.WriteAllText(controlPath+".ack",v);
            }catch(Exception e){File.WriteAllText(controlPath+".error",e.ToString());}
        };
        timer.Start();
        Application.Run(ctx);
        timer.Dispose();
        first?.Dispose();
        second?.Dispose();
    }
    private static Form Make(string title,int left,int top)=>new()
    {
        Text=title,StartPosition=FormStartPosition.Manual,Location=new Point(left,top),
        ClientSize=new Size(440,230),ShowInTaskbar=true,FormBorderStyle=FormBorderStyle.Sizable,
        BackColor=Color.FromArgb(235,246,250)
    };
}
