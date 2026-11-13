using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Diagnostics;

namespace PixelHdd {
    // ---------- 1x pixel canvas: draw at 96 dpi, then nearest-neighbour upscale ----------
    static class Px {
        public static readonly Font F12=new Font("SimSun",12f,FontStyle.Regular,GraphicsUnit.Pixel);
        public static readonly Font F12B=new Font("SimSun",12f,FontStyle.Bold,GraphicsUnit.Pixel);
        public static readonly Color Ink=Color.FromArgb(0,0,0);
        public static readonly Color Val=Color.FromArgb(0,0,110);
        public static readonly Color Gray=Color.FromArgb(110,110,110);
        public static readonly Color Face=SystemColors.Control;
        public static readonly Color SysEdge=Color.FromArgb(105,105,105);
        public static readonly Color Hi=Color.White, Lo=Color.FromArgb(128,128,128);
        public static void Txt(Graphics g,string s,Font f,Color c,int x,int y){
            g.TextRenderingHint=TextRenderingHint.SingleBitPerPixelGridFit;
            using(var b=new SolidBrush(c))g.DrawString(s,f,b,x,y);
        }
        public static void TxtC(Graphics g,string s,Font f,Color c,int x,int y,int w,int h){
            g.TextRenderingHint=TextRenderingHint.SingleBitPerPixelGridFit;
            using(var fmt=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter})
            using(var b=new SolidBrush(c))g.DrawString(s,f,b,new RectangleF(x,y,w,h),fmt);
        }
        public static void TxtL(Graphics g,string s,Font f,Color c,int x,int y,int w,int h){
            g.TextRenderingHint=TextRenderingHint.SingleBitPerPixelGridFit;
            using(var fmt=new StringFormat{Alignment=StringAlignment.Near,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter})
            using(var b=new SolidBrush(c))g.DrawString(s,f,b,new RectangleF(x,y,w,h),fmt);
        }
        public static void Sunken(Graphics g,Rectangle r){
            using(var fb=new SolidBrush(Color.White))g.FillRectangle(fb,r);
            using(var p=new Pen(Lo)){g.DrawLine(p,r.Left,r.Top,r.Right-1,r.Top);g.DrawLine(p,r.Left,r.Top,r.Left,r.Bottom-1);}
            using(var p=new Pen(Hi)){g.DrawLine(p,r.Left,r.Bottom-1,r.Right-1,r.Bottom-1);g.DrawLine(p,r.Right-1,r.Top,r.Right-1,r.Bottom-1);}
        }
        public static void Raised(Graphics g,Rectangle r,bool pressed,bool enabled){
            using(var fb=new SolidBrush(enabled?Face:SystemColors.Control))g.FillRectangle(fb,r);
            Color tl=enabled?(pressed?Lo:Hi):SystemColors.ControlLight;
            Color br=enabled?(pressed?Hi:Lo):SystemColors.ControlLight;
            using(var p=new Pen(tl)){g.DrawLine(p,r.Left,r.Top,r.Right-1,r.Top);g.DrawLine(p,r.Left,r.Top,r.Left,r.Bottom-1);}
            using(var p=new Pen(br)){g.DrawLine(p,r.Left,r.Bottom-1,r.Right-1,r.Bottom-1);g.DrawLine(p,r.Right-1,r.Top,r.Right-1,r.Bottom-1);}
        }
        public static void Group(Graphics g,Rectangle r,string caption){
            using(var p=new Pen(SysEdge))g.DrawRectangle(p,r);
            using(var p=new Pen(Hi))g.DrawRectangle(p,r.X+1,r.Y+1,r.Width-2,r.Height-2);
            if(caption.Length>0){
                int tw=(int)g.MeasureString(caption,F12).Width+2;
                using(var b=new SolidBrush(Face))g.FillRectangle(b,r.X+7,r.Y-5,tw,11);
                Txt(g,caption,F12,Ink,r.X+8,r.Y-7);
            }
        }
    }
    sealed class StateSnapshot {
        public string Mode="识别中",Detail="正在连接硬盘";public bool Offline,Busy,StatsAvailable;
        public int DiskNumber;public int? Temperature;public DateTime TemperatureAt,ConfirmedAt;
        public double ReadRate,WriteRate;public ulong Read,Written;public string Origin="";
        public bool SpaceOk;public double SpaceTotal,SpaceFree;public string SpaceFs="";
        public static StateSnapshot Capture(Controller c){
            var s=new StateSnapshot();lock(c.Gate){
                var st=c.State;
                s.Mode=st.Mode;s.Detail=st.Detail;s.Offline=st.Offline;s.Busy=st.Busy;s.DiskNumber=st.DiskNumber;
                s.Temperature=st.Temperature;s.TemperatureAt=st.TemperatureAt;s.ConfirmedAt=st.ConfirmedAt;
                s.StatsAvailable=st.Stats.Available;s.ReadRate=st.Stats.ReadRate;s.WriteRate=st.Stats.WriteRate;
                s.Read=st.Stats.Read;s.Written=st.Stats.Written;s.Origin=st.Stats.Origin;
            }
            return s;
        }
        public void LoadSpace(string root){
            try{
                if(root==null||root.Length<2){SpaceOk=false;return;}
                var d=new DriveInfo(root);
                if(d.IsReady){SpaceTotal=d.TotalSize;SpaceFree=d.AvailableFreeSpace;SpaceFs=d.DriveFormat;SpaceOk=SpaceTotal>0;}
            }catch{SpaceOk=false;}
        }
    }
    // ---------- the whole window is one painted surface ----------
    sealed class MainWindow:Form {
        Controller controller;readonly NotifyIcon tray;readonly System.Windows.Forms.Timer tick;
        List<DiskTarget> disks=new List<DiskTarget>();
        DiskTarget current;
        internal bool Preview;
        bool exitRequested,sampling,uiPending,listOpen;string error="";
        StateSnapshot view=new StateSnapshot();
        int scale=1;
        readonly double[] rateRead=new double[60],rateWrite=new double[60];int samples;
        int hover=-1,press=-1;
        public static readonly uint ShowMessage=Native.RegisterWindowMessage("PixelHdd.Show.ZRT0E133");
        const int W=360,H=476,M=8;
        readonly List<Rectangle> btnRects=new List<Rectangle>();
        const int B_ON=0,B_IDLE=1,B_OFF=2,B_SET=3,B_HIDE=4,B_EXIT=5;
        static readonly string[] BtnText={"开启","待机","关闭","设置","隐藏","退出"};

        public MainWindow(Controller c,bool startHidden,bool acceptance=false){
            controller=c;Text="PIXEL HDD";AccessibleName=Text;
            AutoScaleMode=AutoScaleMode.None;BackColor=Px.Face;ForeColor=Px.Ink;Font=Px.F12;
            ComputeScale();
            FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;MinimizeBox=true;
            StartPosition=FormStartPosition.CenterScreen;Icon=CreateIcon();
            ClientSize=new Size(W*scale,H*scale);MinimumSize=ClientSize;
            DoubleBuffered=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);
            ResolveDisks();

            var menu=new ContextMenuStrip{ShowImageMargin=false,Font=new Font("SimSun",9f)};
            menu.Items.Add("显示窗口",null,(s,e)=>ShowWindow());menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("开启",null,(s,e)=>Transition("开启"));menu.Items.Add("待机",null,(s,e)=>Transition("待机"));menu.Items.Add("关闭",null,(s,e)=>Transition("关闭"));
            menu.Items.Add(new ToolStripSeparator());menu.Items.Add("设置",null,(s,e)=>Settings());menu.Items.Add("退出",null,(s,e)=>Exit());
            tray=new NotifyIcon{Text="PIXEL HDD",Icon=Icon,Visible=true,ContextMenuStrip=menu};
            tray.DoubleClick+=(s,e)=>ShowWindow();

            tick=new System.Windows.Forms.Timer{Interval=1000};
            tick.Tick+=async(s,e)=>{if(sampling||Preview)return;sampling=true;try{
                StateSnapshot snap=null;
                await Task.Run(()=>{controller.Sample();snap=StateSnapshot.Capture(controller);snap.LoadSpace(current==null?"":current.Root);});
                view=snap;
                for(int i=0;i<59;i++){rateRead[i]=rateRead[i+1];rateWrite[i]=rateWrite[i+1];}
                rateRead[59]=snap.StatsAvailable?snap.ReadRate:0;rateWrite[59]=snap.StatsAvailable?snap.WriteRate:0;if(samples<60)samples++;
                Invalidate();
            }finally{sampling=false;}};
            Shown+=async(s,e)=>{if(startHidden)Hide();if(Preview)return;
                try{await Task.Run(()=>controller.Initialize());}catch(Exception ex){error=ex.Message;}
                view=StateSnapshot.Capture(controller);view.LoadSpace(current==null?"":current.Root);
                if(error.Length>0)view.Detail=error;
                Invalidate();tick.Start();
                if(acceptance)await Task.Run(()=>TestRunner.Run(controller,new string[0]));};
            FormClosing+=(s,e)=>{if(!exitRequested){e.Cancel=true;Hide();}else{tick.Stop();tray.Visible=false;tray.Dispose();}};
        }
        void ComputeScale(){
            int s=1;
            try{using(var g=CreateGraphics())s=(int)Math.Round(g.DpiX/96.0);}catch{}
            if(s<1)s=1;
            try{
                var wa=Screen.PrimaryScreen.WorkingArea;
                while(s>1&&(H*s>wa.Height*0.94||W*s>wa.Width*0.94))s--;
            }catch{}
            scale=s;
        }
        void ResolveDisks(){
            try{disks=Controller.Enumerate();}catch(Exception ex){Store.Log("enum-fail",ex.Message);disks=new List<DiskTarget>();}
            current=null;
            if(disks.Count>0){
                current=disks[0];
                if(controller!=null&&controller.Target!=null)
                    foreach(var d in disks) if(d.Serial==controller.Target.Serial) current=d;
                if(controller==null||controller.Target==null||controller.Target.Serial!=current.Serial)
                    controller=new Controller(current);
            }
        }
        static Icon CreateIcon(){
            using(var b=new Bitmap(32,32)){using(var g=Graphics.FromImage(b)){
                g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.None;
                using(var body=new SolidBrush(Color.FromArgb(235,237,241)))g.FillRectangle(body,3,9,26,16);
                using(var e=new Pen(Color.FromArgb(60,70,86)))g.DrawRectangle(e,3,9,25,15);
                using(var pl=new SolidBrush(Color.FromArgb(190,196,206)))g.FillRectangle(pl,6,13,12,7);
                using(var led=new SolidBrush(Color.FromArgb(20,130,80)))g.FillRectangle(led,21,18,4,3);}
                return Icon.FromHandle(b.GetHicon());}
        }
        // ---------- geometry ----------
        static Rectangle SelRect(){return new Rectangle(44,4,W-44-M,20);}
        Rectangle ListRect(){
            int rows=Math.Min(6,Math.Max(1,disks.Count));
            return new Rectangle(44,24,W-44-M,rows*20+2);
        }
        // ---------- interaction ----------
        protected override void OnMouseMove(MouseEventArgs e){
            base.OnMouseMove(e);
            int h=HitTest(e.X/scale,e.Y/scale);
            if(h!=hover){hover=h;Invalidate();}
        }
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(hover!=-1){hover=-1;Invalidate();}}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);press=HitTest(e.X/scale,e.Y/scale);Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e){
            base.OnMouseUp(e);
            int h=HitTest(e.X/scale,e.Y/scale);
            if(h==press&&h>=0)Act(h);
            press=-1;Invalidate();
        }
        int HitTest(int x,int y){
            if(listOpen){
                var list=ListRect();
                if(list.Contains(x,y)){
                    int i=(y-list.Top-1)/20;
                    if(i>=0&&i<disks.Count)return 100+i;
                }
                if(SelRect().Contains(x,y))return 90;
                return -2;
            }
            if(SelRect().Contains(x,y)&&disks.Count>1)return 90;
            for(int i=0;i<btnRects.Count;i++)if(btnRects[i].Contains(x,y))return i;
            return -1;
        }
        void Act(int h){
            if(h==-2){if(listOpen){listOpen=false;Invalidate();}return;}
            if(h==90){if(disks.Count>1){listOpen=!listOpen;Invalidate();}return;}
            if(h>=100){int i=h-100;if(i>=0&&i<disks.Count){listOpen=false;SwitchDisk(disks[i]);}return;}
            if(h==B_ON)Transition("开启");
            else if(h==B_IDLE)Transition("待机");
            else if(h==B_OFF)Transition("关闭");
            else if(h==B_SET)Settings();
            else if(h==B_HIDE)Hide();
            else if(h==B_EXIT)Exit();
        }
        async void SwitchDisk(DiskTarget target){
            if(target==current||uiPending||view.Busy)return;
            uiPending=true;Invalidate();
            try{
                var next=new Controller(target);
                await Task.Run(()=>next.Initialize());
                controller=next;current=target;
                for(int i=0;i<60;i++){rateRead[i]=0;rateWrite[i]=0;}samples=0;
                view=StateSnapshot.Capture(controller);view.LoadSpace(target.Root);error="";
            }catch(Exception ex){error=ex.Message;MessageBox.Show(this,ex.Message,"切换硬盘失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            finally{uiPending=false;Invalidate();}
        }
        async void Transition(string mode){
            if(uiPending||view.Busy||current==null)return;
            uiPending=true;Invalidate();
            try{await Task.Run(()=>controller.SetMode(mode));error="";}
            catch(Exception ex){error=ex.Message;MessageBox.Show(this,ex.Message,"状态未按请求切换",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            finally{uiPending=false;view=StateSnapshot.Capture(controller);view.LoadSpace(current==null?"":current.Root);Invalidate();}
        }
        public void ShowWindow(){Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Activate();}
        void Exit(){if(view.Busy){MessageBox.Show(this,"请等待当前状态切换完成。","正在操作",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}exitRequested=true;Close();}
        void Settings(){
            using(var f=new Form{Text="PIXEL HDD 设置",AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(360*scale,150*scale),
                StartPosition=FormStartPosition.CenterParent,BackColor=Px.Face,ForeColor=Px.Ink,
                FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,ShowIcon=false,Font=Px.F12}){
                var auto=new CheckBox{Text="登录后自动启动并驻留托盘",Checked=controller.Preferences.AutoStart,
                    Location=new Point(12*scale,12*scale),AutoSize=true,Font=Px.F12};
                var note=new Label{Text="待机 / 关闭时保留最近温度。\n隐藏后可从托盘恢复；退出保留当前硬盘状态。",
                    AutoSize=true,Location=new Point(12*scale,36*scale),Font=Px.F12,ForeColor=Px.Gray};
                var logs=new LinkLabel{Text="打开日志与许可目录",Location=new Point(12*scale,80*scale),AutoSize=true,Font=Px.F12};
                var ok=new Button{Text="确定",DialogResult=DialogResult.OK,Location=new Point(186*scale,112*scale),Size=new Size(74*scale,24*scale),FlatStyle=FlatStyle.System,Font=Px.F12};
                var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel,Location=new Point(268*scale,112*scale),Size=new Size(74*scale,24*scale),FlatStyle=FlatStyle.System,Font=Px.F12};
                logs.LinkClicked+=(s,e)=>Process.Start("explorer.exe",Store.Root);
                ok.Click+=(s,e)=>{try{if(auto.Checked!=controller.Preferences.AutoStart)controller.SetAutoStart(auto.Checked);f.DialogResult=DialogResult.OK;f.Close();}
                    catch(Exception ex){MessageBox.Show(f,ex.Message,"设置未保存",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
                f.Controls.AddRange(new Control[]{auto,note,logs,ok,cancel});
                f.AcceptButton=ok;f.CancelButton=cancel;f.ShowDialog(this);
            }
        }
        protected override void WndProc(ref Message m){
            if((uint)m.Msg==ShowMessage){ShowWindow();return;}
            base.WndProc(ref m);
        }
        // ---------- preview hook ----------
        internal void PreviewMode(int state){
            view=new StateSnapshot{Mode=state==0?"开启":state==1?"待机":"关闭记录",Offline=state==2,DiskNumber=0,
                Temperature=state==1?31:36,TemperatureAt=DateTime.Now.AddSeconds(-38),ConfirmedAt=DateTime.Now.AddSeconds(-2),
                StatsAvailable=state==0,ReadRate=state==0?412.5*1024*1024:0,WriteRate=state==0?88.2*1024*1024:0,
                Read=12069828608,Written=269606912,Origin="首次观测 11-13 12:38"};
            view.Detail=state==0?"已确认 · 主轴旋转 · 联机可读写":state==1?"已确认 · 磁头卸载 · 盘片继续旋转":"上次记录 · 本次未查询";
            view.SpaceOk=true;view.SpaceTotal=12000138625024d;view.SpaceFree=7815000000000d;view.SpaceFs="NTFS";
            if(current==null){current=new DiskTarget{Number=0,Model="ST12000NM000J",Serial="ZRT0E133",Size=12000138625024L,Root="I:\\"};disks=new List<DiskTarget>{current};}
            for(int i=0;i<60;i++){rateRead[i]=state==0?Math.Abs(Math.Sin(i*0.7))*380*1024*1024+40*1024*1024:0;rateWrite[i]=state==0?Math.Abs(Math.Cos(i*0.9))*80*1024*1024+8*1024*1024:0;}
            samples=60;Invalidate();
        }
        // ---------- paint ----------
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);
            using(var canvas=new Bitmap(W,H)){
                using(var g=Graphics.FromImage(canvas)){
                    g.Clear(Px.Face);g.SmoothingMode=SmoothingMode.None;
                    PaintAll(g);
                }
                e.Graphics.InterpolationMode=InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode=PixelOffsetMode.Half;
                e.Graphics.DrawImage(canvas,new Rectangle(0,0,ClientSize.Width,ClientSize.Height),0,0,W,H,GraphicsUnit.Pixel);
            }
        }
        void PaintAll(Graphics g){
            var s=view;
            // disk selector
            var sel=SelRect();
            Px.Txt(g,"硬盘",Px.F12,Px.Ink,6,8);
            Px.Sunken(g,sel);
            string selText=current==null?"未找到可控制的硬盘":current.Label;
            Px.TxtL(g,selText,Px.F12,current==null?Px.Gray:Px.Val,sel.X+4,sel.Y,sel.Width-24,sel.Height);
            if(disks.Count>1){
                var arrow=new Rectangle(sel.Right-18,sel.Y+2,16,16);
                Px.Raised(g,arrow,!listOpen,true);
                Px.TxtC(g,listOpen?"▲":"▼",Px.F12,Px.Ink,arrow.X,arrow.Y,arrow.Width,arrow.Height);
            }
            // identity
            Px.Group(g,new Rectangle(M,36,W-2*M,74),"设备信息");
            IdRow(g,16,50,"型号",current==null?"—":(current.Model.Length>0?current.Model:"未知型号"),W-2*M-46);
            IdRow(g,16,68,"容量",current==null?"—":Controller.Human(current.Size),150);
            IdRow(g,190,68,"接口",BusName(current==null?(ushort)0:current.BusType),168);
            IdRow(g,16,86,"序列号",current==null||current.Serial.Length==0?"—":current.Serial,150);
            IdRow(g,190,86,"磁盘号",current==null?"—":current.Number+"   "+current.Root,168);

            // state
            Px.Group(g,new Rectangle(M,114,W-2*M,90),"运行状态");
            Row(g,16,130,"状态",s.Busy?"切换中":s.Mode,146);
            Row(g,160,130,"确认",s.ConfirmedAt==DateTime.MinValue?"—":s.ConfirmedAt.ToString("MM-dd HH:mm"),192);
            Row(g,16,148,"温度",s.Temperature.HasValue?s.Temperature.Value+" ℃":"—",146);
            Row(g,160,148,"时间",s.TemperatureAt==DateTime.MinValue?"—":s.TemperatureAt.ToString("HH:mm")+(s.Offline?" 前":""),192);
            Px.Txt(g,s.Detail,Px.F12,error.Length>0?Color.FromArgb(170,0,0):Px.Gray,18,166);
            Lamp(g,18,184,"开启",s.Mode=="开启"&&!s.Offline,Color.FromArgb(0,170,60));
            Lamp(g,94,184,"待机",s.Mode=="待机",Color.FromArgb(225,160,0));
            Lamp(g,170,184,"关闭",s.Mode=="关闭"||s.Mode=="关闭记录"||s.Offline,Color.FromArgb(215,40,40));

            // transfer
            Px.Group(g,new Rectangle(M,208,W-2*M,74),"传输");
            Row(g,16,224,"读取",s.StatsAvailable?Controller.Human(s.ReadRate)+"/s":"—",130);
            Row(g,170,224,"写入",s.StatsAvailable?Controller.Human(s.WriteRate)+"/s":"—",172);
            Row(g,16,242,"本次读",Controller.Human(s.Read),130);
            Row(g,170,242,"本次写",Controller.Human(s.Written),172);
            Row(g,16,260,"口径",s.Origin,W-2*M-26);

            // volume
            Px.Group(g,new Rectangle(M,286,W-2*M,66),"卷空间");
            Row(g,16,302,"文件系统",s.SpaceOk?s.SpaceFs:"—",130);
            Row(g,170,302,"使用率",s.SpaceOk?((s.SpaceTotal-s.SpaceFree)/s.SpaceTotal*100).ToString("0.0")+" %":"—",172);
            Row(g,16,320,"总容量",s.SpaceOk?Controller.Human(s.SpaceTotal):"—",130);
            Row(g,170,320,"可用",s.SpaceOk?Controller.Human(s.SpaceFree):"—",172);
            UsageBar(g,new Rectangle(16,336,W-2*M-24,10),s);

            // chart
            Px.Group(g,new Rectangle(M,356,W-2*M,88),"传输曲线（60 秒）");
            Chart(g,new Rectangle(34,378,W-2*M-46,58),s);

            // buttons
            btnRects.Clear();
            int bw=52,bh=22,gap=4;
            int total=6*bw+5*gap,bx=(W-total)/2,by=448;
            for(int i=0;i<6;i++){
                var r=new Rectangle(bx+i*(bw+gap),by,bw,bh);
                btnRects.Add(r);
                bool enabled=BtnEnabled(i,s);
                Px.Raised(g,r,press==i,enabled);
                Px.TxtC(g,BtnText[i],Px.F12,enabled?Px.Ink:Color.FromArgb(130,130,130),r.X,r.Y,r.Width,r.Height);
            }
            if(listOpen)DrawList(g);
        }
        bool BtnEnabled(int i,StateSnapshot s){
            if(uiPending)return false;
            if(i==B_ON)return !s.Busy;
            if(i==B_IDLE)return !s.Busy&&!s.Offline;
            if(i==B_OFF)return !s.Busy&&!s.Offline&&controller.Preferences.OffValidated;
            return true;
        }
        static string BusName(ushort bus){
            switch(bus){case 3:return "ATA";case 4:return "SATA";case 8:return "RAID";case 10:return "SAS";case 11:return "SATA";}
            return bus==0?"—":"总线 "+bus;
        }
        void IdRow(Graphics g,int x,int y,string label,string value,int valW){
            Px.Txt(g,label,Px.F12,Px.Ink,x,y);
            var r=new Rectangle(x+46,y-2,Math.Max(40,valW-46),16);
            Px.Sunken(g,r);
            Px.TxtL(g,value,Px.F12,Px.Val,r.X+3,r.Y,r.Width-6,r.Height);
        }
        void Row(Graphics g,int x,int y,string label,string value,int w){
            Px.Txt(g,label,Px.F12,Px.Ink,x,y);
            var r=new Rectangle(x+40,y-2,Math.Max(40,w-40),16);
            Px.Sunken(g,r);
            Px.TxtL(g,value,Px.F12,Px.Val,r.X+3,r.Y,r.Width-6,r.Height);
        }
        void Lamp(Graphics g,int x,int y,string caption,bool lit,Color col){
            var body=new Rectangle(x,y,14,14);
            using(var b=new SolidBrush(lit?col:Color.FromArgb(168,170,176)))g.FillRectangle(b,body);
            using(var p=new Pen(Color.FromArgb(70,70,70)))g.DrawRectangle(p,body);
            using(var h=new SolidBrush(lit?Color.FromArgb(235,255,235):Color.FromArgb(214,214,218)))g.FillRectangle(h,x+2,y+2,4,3);
            Px.Txt(g,caption,Px.F12,lit?Color.FromArgb(0,0,0):Px.Gray,x+17,y+1);
        }
        void UsageBar(Graphics g,Rectangle r,StateSnapshot s){
            double pct=s.SpaceOk&&s.SpaceTotal>0?(s.SpaceTotal-s.SpaceFree)/s.SpaceTotal:0;
            using(var bg=new SolidBrush(Color.White))g.FillRectangle(bg,r);
            using(var fp=new SolidBrush(pct>0.9?Color.FromArgb(200,40,40):Color.FromArgb(40,90,190)))
                g.FillRectangle(fp,r.X,r.Y,(int)((r.Width-2)*pct),r.Height);
            using(var tick=new Pen(Color.FromArgb(150,150,150)))
                for(int i=1;i<10;i++){int tx=r.X+r.Width*i/10;g.DrawLine(tick,tx,r.Y+1,tx,r.Bottom-2);}
            using(var edge=new Pen(Color.FromArgb(90,90,90)))g.DrawRectangle(edge,r.X,r.Y,r.Width-1,r.Height-1);
        }
        void Chart(Graphics g,Rectangle r,StateSnapshot s){
            using(var bg=new SolidBrush(Color.White))g.FillRectangle(bg,r);
            using(var grid=new Pen(Color.FromArgb(198,198,198)))
                for(int i=1;i<4;i++){int gy=r.Y+r.Height*i/4;g.DrawLine(grid,r.X,gy,r.Right-1,gy);}
            using(var edge=new Pen(Color.FromArgb(90,90,90)))g.DrawRectangle(edge,r.X,r.Y,r.Width-1,r.Height-1);
            double peak=1024*1024;for(int i=0;i<samples;i++){if(rateRead[i]>peak)peak=rateRead[i];if(rateWrite[i]>peak)peak=rateWrite[i];}
            // y axis: peak at the top, half and zero marks
            Px.Txt(g,Controller.Human(peak)+"/s",Px.F12,Px.Gray,r.X+2,r.Y-14);
            Px.Txt(g,Controller.Human(peak/2),Px.F12,Px.Gray,r.X+2,r.Y+r.Height/2-6);
            Px.Txt(g,"0",Px.F12,Px.Gray,r.X+2,r.Bottom-12);
            if(samples>1){
                int plotX=r.X+1, plotW=r.Width-2, baseY=r.Y+r.Height-1, plotH=r.Height-3;
                // one column per pixel: linear interpolation between the 1 s samples gives a fine curve
                using(var fill=new SolidBrush(Color.FromArgb(168,198,235)))
                using(var rp=new Pen(Color.FromArgb(28,78,176),1f))
                using(var wp=new Pen(Color.FromArgb(196,104,0),1f)){
                    int prevRy=baseY, prevWy=baseY;
                    for(int x=0;x<plotW;x++){
                        double pos=(double)x/(plotW-1)*(samples-1);
                        int i0=(int)pos; int i1=Math.Min(i0+1,samples-1); double t=pos-i0;
                        double rv=rateRead[i0]+(rateRead[i1]-rateRead[i0])*t;
                        double wv=rateWrite[i0]+(rateWrite[i1]-rateWrite[i0])*t;
                        int ry=baseY-(int)(Math.Min(1,rv/peak)*plotH);
                        int wy=baseY-(int)(Math.Min(1,wv/peak)*plotH);
                        if(ry<baseY)g.FillRectangle(fill,plotX+x,ry,1,baseY-ry);
                        if(x==0){prevRy=ry;prevWy=wy;}
                        g.DrawLine(rp,plotX+x-1,prevRy,plotX+x,ry);
                        g.DrawLine(wp,plotX+x-1,prevWy,plotX+x,wy);
                        prevRy=ry;prevWy=wy;
                    }
                }
                // legend
                using(var lb=new SolidBrush(Color.FromArgb(230,255,255,255)))g.FillRectangle(lb,r.X+2,r.Bottom-15,58,13);
                Px.Txt(g,"■读",Px.F12,Color.FromArgb(28,78,176),r.X+4,r.Bottom-15);
                Px.Txt(g,"■写",Px.F12,Color.FromArgb(196,104,0),r.X+28,r.Bottom-15);
            } else Px.TxtC(g,"—",Px.F12,Px.Gray,r.X,r.Y,r.Width,r.Height);
            if(!s.StatsAvailable)Px.TxtC(g,s.Offline?"硬盘已关闭":"暂无采样",Px.F12,Px.Gray,r.X,r.Y,r.Width,r.Height);
        }
        void DrawList(Graphics g){
            var list=ListRect();
            using(var sh=new SolidBrush(Color.FromArgb(70,0,0,0)))g.FillRectangle(sh,list.X+2,list.Y+2,list.Width,list.Height);
            using(var b=new SolidBrush(Color.White))g.FillRectangle(b,list);
            using(var p=new Pen(Px.SysEdge))g.DrawRectangle(p,list.X,list.Y,list.Width-1,list.Height-1);
            for(int i=0;i<disks.Count&&i<6;i++){
                var row=new Rectangle(list.X+1,list.Y+1+i*20,list.Width-2,20);
                bool isCur=(current!=null&&disks[i].Number==current.Number);
                bool isHover=(hover==100+i);
                if(isCur||isHover)using(var hb=new SolidBrush(Color.FromArgb(0,0,128)))g.FillRectangle(hb,row);
                Px.TxtL(g,disks[i].Label,Px.F12,isCur||isHover?Color.White:Px.Ink,row.X+4,row.Y,row.Width-8,row.Height);
            }
        }
    }
    static class Program {
        [STAThread] static void Main(string[] args) {
            try {
                Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                bool created;using(var mutex=new Mutex(true,"Local\\PixelHdd.ZRT0E133",out created)) {
                    if(!created){Native.PostMessage(new IntPtr(0xffff),MainWindow.ShowMessage,IntPtr.Zero,IntPtr.Zero);return;}
                    var controller=new Controller();
                    if(args.Contains("--probe")){controller.Probe();return;}
                    Application.Run(new MainWindow(controller,args.Contains("--tray"),args.Contains("--acceptance")));
                }
            }catch(Exception ex){Store.Log("fatal",ex.ToString());MessageBox.Show(ex.Message,"PIXEL HDD",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
}
