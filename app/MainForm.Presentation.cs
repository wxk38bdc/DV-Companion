namespace DVCompanion;

public sealed partial class MainForm
{
    readonly TabControl workspaceTabs=new WorkspaceTabs{Dock=DockStyle.Fill,Margin=Padding.Empty};
    (Button browse,Button open,Button inspect,Button save,Button snapshot,Button restore,Button export,Button folder) BuildPresentation()
    {
        SuspendLayout();
        var shell=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(24,20,24,12),ColumnCount=1,RowCount=4,BackColor=Presentation.Canvas,Margin=Padding.Empty};
        shell.RowStyles.Add(new(SizeType.Absolute,140));shell.RowStyles.Add(new(SizeType.Absolute,98));shell.RowStyles.Add(new(SizeType.Percent,100));shell.RowStyles.Add(new(SizeType.Absolute,30));Controls.Add(shell);
        var header=Presentation.Surface(Presentation.Navy,new Padding(24,18,22,18));header.Stroke=Color.Transparent;header.Margin=new Padding(0,0,0,12);header.Radius=18;shell.Controls.Add(header,0,0);
        var top=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty,BackColor=Presentation.Navy};top.RowStyles.Add(new(SizeType.Percent,100));top.ColumnStyles.Add(new(SizeType.Absolute,78));top.ColumnStyles.Add(new(SizeType.Percent,100));top.ColumnStyles.Add(new(SizeType.Absolute,400));header.Controls.Add(top);
        var brand=new PictureBox{Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Margin=new Padding(0,6,14,6),MaximumSize=new Size(64,64),BackColor=Presentation.Navy};
        using(var stream=typeof(MainForm).Assembly.GetManifestResourceStream("DVCompanion.Brand.png")!)using(var image=new Bitmap(stream))brand.Image=new Bitmap(image);
        Disposed+=(_,_)=>{brand.Image?.Dispose();Icon?.Dispose();};top.Controls.Add(brand,0,0);
        var title=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=Padding.Empty,Padding=new Padding(0,0,0,0),BackColor=Presentation.Navy};title.RowStyles.Add(new(SizeType.Absolute,58));title.RowStyles.Add(new(SizeType.Percent,100));
        var name=Presentation.Label("DV Companion",20,true,Color.White);name.Font=new Font("Segoe UI",20,FontStyle.Bold);title.Controls.Add(name,0,0);title.Controls.Add(Presentation.Label("设备检查 · 可靠退出 · 配置预检 · 本地诊断",9,false,Color.FromArgb(175,198,214)),0,1);top.Controls.Add(title,1,0);
        var install=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=new Padding(10,2,0,0),BackColor=Presentation.Navy};install.RowStyles.Add(new(SizeType.Absolute,28));install.RowStyles.Add(new(SizeType.Absolute,44));install.RowStyles.Add(new(SizeType.Percent,100));install.Controls.Add(Presentation.Label("DV 安装目录",9,false,Color.FromArgb(175,198,214)),0,0);
        var location=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty,BackColor=Presentation.Navy};location.RowStyles.Add(new(SizeType.Percent,100));location.ColumnStyles.Add(new(SizeType.Percent,100));location.ColumnStyles.Add(new(SizeType.Absolute,82));
        var path=Presentation.Surface(Color.FromArgb(239,245,249),new Padding(10,8,8,5));path.Radius=8;path.Stroke=Color.Transparent;path.Margin=new Padding(0,0,8,0);root.BorderStyle=BorderStyle.None;root.BackColor=path.Surface;root.ForeColor=Presentation.Ink;root.Font=new Font("Segoe UI",9.5f);path.Controls.Add(root);location.Controls.Add(path,0,0);
        var browse=new StyledButton{Text="选择…",Dock=DockStyle.Fill};Presentation.Button(browse,"accent");browse.MinimumSize=Size.Empty;browse.Padding=Padding.Empty;browse.Margin=Padding.Empty;location.Controls.Add(browse,1,0);install.Controls.Add(location,0,1);top.Controls.Add(install,2,0);

        var statusCard=Presentation.Surface(Color.White,new Padding(20,12,16,12));statusCard.Margin=new Padding(0,0,0,12);shell.Controls.Add(statusCard,0,1);
        var stateLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty,BackColor=Color.White};stateLayout.RowStyles.Add(new(SizeType.Percent,100));stateLayout.ColumnStyles.Add(new(SizeType.Absolute,14));stateLayout.ColumnStyles.Add(new(SizeType.Percent,100));statusCard.Controls.Add(stateLayout);
        var accent=new RoundedSurface{Dock=DockStyle.Fill,Surface=Presentation.Accent,Stroke=Color.Transparent,Radius=2,Margin=new Padding(0,1,10,1)};stateLayout.Controls.Add(accent,0,0);
        var stateCopy=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty,BackColor=Color.White};stateCopy.RowStyles.Add(new(SizeType.Absolute,36));stateCopy.RowStyles.Add(new(SizeType.Percent,100));status.Font=new Font("Microsoft YaHei UI",13,FontStyle.Bold);status.ForeColor=Presentation.Ink;status.Margin=Padding.Empty;detail.Font=new Font("Microsoft YaHei UI",9);detail.ForeColor=Presentation.Muted;detail.Margin=new Padding(0,2,0,0);detail.MaximumSize=Size.Empty;detail.Dock=DockStyle.Fill;detail.AutoSize=false;stateCopy.Controls.Add(status,0,0);stateCopy.Controls.Add(detail,0,1);stateLayout.Controls.Add(stateCopy,1,0);

        var workspace=Presentation.Surface(Color.White,new Padding(12,10,12,12));shell.Controls.Add(workspace,0,2);
        var pages=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty,BackColor=Color.White};pages.RowStyles.Add(new(SizeType.Absolute,46));pages.RowStyles.Add(new(SizeType.Percent,100));workspace.Controls.Add(pages);
        var navigation=Presentation.Flow();navigation.Padding=new Padding(3,0,0,0);pages.Controls.Add(navigation,0,0);pages.Controls.Add(workspaceTabs,0,1);
        var navigationButtons=new List<StyledButton>();
        foreach(string caption in new[]{"设备与运行","配置检查","诊断记录"})
        {
            int index=navigationButtons.Count;var item=new StyledButton{Text=caption,AccessibleRole=AccessibleRole.PageTab};Presentation.Button(item,index==0?"tabSelected":"tab");item.MinimumSize=new Size(148,38);item.Margin=new Padding(0,0,8,0);item.Click+=(_,_)=>{workspaceTabs.SelectedIndex=index;workspaceTabs.Focus();};navigationButtons.Add(item);navigation.Controls.Add(item);
        }
        workspaceTabs.SelectedIndexChanged+=(_,_)=>{for(int i=0;i<navigationButtons.Count;i++){navigationButtons[i].Tone=i==workspaceTabs.SelectedIndex?"tabSelected":"tab";navigationButtons[i].Invalidate();}};
        var live=new TabPage("设备与运行"){Padding=new Padding(14,12,14,2),BackColor=Color.White};workspaceTabs.TabPages.Add(live);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Margin=Padding.Empty,BackColor=Color.White};layout.RowStyles.Add(new(SizeType.Absolute,54));layout.RowStyles.Add(new(SizeType.Absolute,34));layout.RowStyles.Add(new(SizeType.Percent,43));layout.RowStyles.Add(new(SizeType.Absolute,42));layout.RowStyles.Add(new(SizeType.Percent,57));layout.RowStyles.Add(new(SizeType.Absolute,54));live.Controls.Add(layout);
        foreach(var button in new[]{start,stop,refresh,recover,force})Presentation.Button(button,button==start?"primary":button==force?"danger":"neutral");
        var actions=Presentation.Flow();actions.Controls.AddRange([start,stop,refresh,patched]);patched.Font=new Font("Microsoft YaHei UI",9);patched.ForeColor=Presentation.Muted;patched.Margin=new Padding(4,10,0,0);layout.Controls.Add(actions,0,0);
        layout.Controls.Add(Presentation.Section("相机","Windows 已识别不代表 DV 已成功打开设备"),0,1);layout.Controls.Add(cameras,0,2);layout.Controls.Add(Presentation.Section("DV 进程","本工具只自动管理自己启动的会话"),0,3);layout.Controls.Add(processes,0,4);
        var recovery=Presentation.Flow();recovery.Padding=new Padding(0,14,0,0);recovery.Controls.AddRange([recover,force]);layout.Controls.Add(recovery,0,5);

        var configTab=new TabPage("配置检查"){Padding=new Padding(14,12,14,2),BackColor=Color.White};workspaceTabs.TabPages.Add(configTab);
        var cl=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Margin=Padding.Empty,BackColor=Color.White};cl.RowStyles.Add(new(SizeType.Absolute,54));cl.RowStyles.Add(new(SizeType.Absolute,58));cl.RowStyles.Add(new(SizeType.Absolute,34));cl.RowStyles.Add(new(SizeType.Percent,62));cl.RowStyles.Add(new(SizeType.Absolute,38));cl.RowStyles.Add(new(SizeType.Percent,38));configTab.Controls.Add(cl);
        var ca=Presentation.Flow();var open=new StyledButton{Text="打开 XML…"};var inspect=new StyledButton{Text="检查配置"};var save=new StyledButton{Text="保存可恢复项目…"};var snapshot=new StyledButton{Text="读取后台配置"};var restore=new StyledButton{Text="恢复此项目到 DV"};foreach(var b in new[]{open,inspect,save,snapshot,restore})Presentation.Button(b,b==save?"primary":b==restore?"accent":"neutral");ca.Controls.AddRange([open,inspect,save,snapshot,restore]);cl.Controls.Add(ca,0,0);
        var fileInfo=Presentation.Surface(Presentation.Soft,new Padding(12,8,12,8));fileInfo.Radius=8;fileInfo.Margin=new Padding(0,0,0,4);configPath.AutoSize=false;configPath.Dock=DockStyle.Fill;configPath.MaximumSize=Size.Empty;configPath.ForeColor=Presentation.Muted;configPath.Font=new Font("Microsoft YaHei UI",9);configPath.TextAlign=ContentAlignment.MiddleLeft;fileInfo.Controls.Add(configPath);cl.Controls.Add(fileInfo,0,1);
        cl.Controls.Add(Presentation.Section("项目 XML","保存时读取当前 DV；恢复时使用下方 XML"),0,2);var code=Presentation.Surface(Presentation.Soft,new Padding(12));code.Radius=8;editor.BorderStyle=BorderStyle.None;editor.BackColor=Presentation.Soft;editor.ForeColor=Color.FromArgb(41,67,87);editor.Font=new Font("Consolas",10);code.Controls.Add(editor);cl.Controls.Add(code,0,3);
        cl.Controls.Add(Presentation.Section("检查结果","设备、模块与同步引用"),0,4);cl.Controls.Add(findings,0,5);

        var diagnosticTab=new TabPage("诊断记录"){Padding=new Padding(14,12,14,2),BackColor=Color.White};workspaceTabs.TabPages.Add(diagnosticTab);
        var dl=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty,BackColor=Color.White};dl.RowStyles.Add(new(SizeType.Absolute,54));dl.RowStyles.Add(new(SizeType.Absolute,32));dl.RowStyles.Add(new(SizeType.Absolute,50));dl.RowStyles.Add(new(SizeType.Absolute,34));dl.RowStyles.Add(new(SizeType.Percent,100));diagnosticTab.Controls.Add(dl);
        var da=Presentation.Flow();var export=new StyledButton{Text="保存诊断包"};var folder=new StyledButton{Text="打开记录文件夹"};Presentation.Button(export,"primary");Presentation.Button(folder);da.Controls.AddRange([export,folder]);dl.Controls.Add(da,0,0);dump.Font=new Font("Microsoft YaHei UI",9);dump.ForeColor=Presentation.Ink;dump.Margin=new Padding(0,3,0,0);dl.Controls.Add(dump,0,1);
        var privacy=Presentation.Surface(Presentation.Soft,new Padding(12,8,12,8));privacy.Radius=8;privacy.Margin=new Padding(0,0,0,5);var privacyCopy=Presentation.Label("所有记录均保存在本机。诊断包可能包含序列号、文件路径和配置；分享前请检查。",9,false,Presentation.Muted);privacyCopy.Dock=DockStyle.Fill;privacyCopy.AutoSize=false;privacyCopy.TextAlign=ContentAlignment.MiddleLeft;privacy.Controls.Add(privacyCopy);dl.Controls.Add(privacy,0,2);
        dl.Controls.Add(Presentation.Section("运行记录","操作与故障提示"),0,3);var log=Presentation.Surface(Presentation.Soft,new Padding(12));log.Radius=8;activity.BorderStyle=BorderStyle.None;activity.BackColor=Presentation.Soft;activity.ForeColor=Color.FromArgb(60,82,101);log.Controls.Add(activity);dl.Controls.Add(log,0,4);
        foreach(var grid in new[]{cameras,processes,findings})Presentation.Grid(grid);Presentation.Empty(cameras,"尚未发现相机，请检查连接后刷新");Presentation.Empty(processes,"当前没有 DV 进程");Presentation.Empty(findings,"打开 XML 后，检查结果将在这里显示");
        var footer=Presentation.Label("0.2.1  ·  默认不覆盖原软件  ·  不自动强杀或重启录制任务",8.5f,false,Presentation.Muted);footer.Dock=DockStyle.Fill;footer.AutoSize=false;footer.TextAlign=ContentAlignment.MiddleLeft;footer.Padding=new Padding(3,4,0,0);shell.Controls.Add(footer,0,3);
        void Stretch(Control control)
        {
            if(control is TableLayoutPanel table&&table.ColumnCount==1&&table.ColumnStyles.Count==0)table.ColumnStyles.Add(new(SizeType.Percent,100));
            foreach(Control child in control.Controls)Stretch(child);
        }
        Stretch(shell);
        cl.SizeChanged+=(_,_)=>{bool compact=cl.ClientSize.Height<430;cl.RowStyles[3].Height=compact?50:62;cl.RowStyles[5].Height=compact?50:38;};
        detail.AutoEllipsis=configPath.AutoEllipsis=true;
        ResumeLayout(true);return(browse,open,inspect,save,snapshot,restore,export,folder);
    }
}
