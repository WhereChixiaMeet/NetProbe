using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Windows.Forms;
using NetProbe;

static class Regression {
 static BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
 static int checks;
 static void Assert(bool ok, string text) { if(!ok) throw new Exception(text); checks++; }
 static void Set(object x,string n,object v) { x.GetType().GetField(n,F).SetValue(x,v); }
 static object Call(object x,string n,params object[] a) { return x.GetType().GetMethod(n,F).Invoke(x,a); }
 static bool Locked(MainForm f) { return (bool)typeof(MainForm).GetProperty("TunSettingsLocked",F).GetValue(f,null); }
 static IEnumerable<Control> All(Control c) { yield return c; foreach(Control k in c.Controls) foreach(var x in All(k)) yield return x; }
 [STAThread] static int Main() {
  try {
   Application.EnableVisualStyles(); Logger.ToFile=false;
   var f=(MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));
   // Initialize only the Form base, leaving startup, network probing and OS proxy settings untouched.
   var init=new DynamicMethod("InitFormBase",null,new[]{typeof(MainForm)},typeof(Regression),true);
   var il=init.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call,typeof(Form).GetConstructor(Type.EmptyTypes)); il.Emit(OpCodes.Ret);
   ((Action<MainForm>)init.CreateDelegate(typeof(Action<MainForm>)))(f);
   var r=(Router)FormatterServices.GetUninitializedObject(typeof(Router));
   var x=(XrayManager)FormatterServices.GetUninitializedObject(typeof(XrayManager));
   Set(f,"router",r); Set(f,"xm",x); Set(f,"st",new Settings()); Set(f,"curPage",-1);
   foreach(RouteState rs in Enum.GetValues(typeof(RouteState))) foreach(XState xs in Enum.GetValues(typeof(XState))) {
    r.State=rs; x.State=xs;
    Assert(Locked(f)==(rs!=RouteState.Off || xs!=XState.Off),"state matrix "+rs+"/"+xs);
   }
   r.State=RouteState.Off; x.State=XState.Off;
   Set(r,"pendingConnects",1); Assert(Locked(f),"queued router connection"); Set(r,"pendingConnects",0);
   Set(x,"pendingConnects",1); Assert(Locked(f),"queued Xray connection"); Set(x,"pendingConnects",0);
   Set(f,"pendingXConnect","installing-core"); Assert(Locked(f),"core download pending"); Set(f,"pendingXConnect",null);
   Assert(!Locked(f),"offline edits unlocked");
   AppLists.Set(AppLists.HybridApps,new[]{"browser.exe"});
   AppLists.Set(AppLists.HybridExcl,new[]{"game.exe"});
   AppLists.Set(AppLists.VpnOnlyApps,new string[0]);
   Call(f,"AddToList",AppLists.HybridExcl,"browser.exe");
   Assert(!AppLists.Get(AppLists.HybridApps).Contains("browser.exe") && AppLists.Get(AppLists.HybridExcl).Contains("browser.exe"),"offline move to exclude");
   Call(f,"AddToList",AppLists.VpnOnlyApps,"browser.exe");
   Assert(!AppLists.Get(AppLists.HybridExcl).Contains("browser.exe") && AppLists.Get(AppLists.VpnOnlyApps).Contains("browser.exe"),"offline move to strict");
   Call(f,"RemoveFromList",AppLists.VpnOnlyApps,new List<string>{"browser.exe"});
   Assert(AppLists.Get(AppLists.VpnOnlyApps).Count==0,"offline delete");
   var src=new ListCard{Key=AppLists.HybridApps}; var dst=new ListCard{Key=AppLists.HybridExcl};
   Set(f,"listCards",new[]{src,dst});
   int notices=0; string noticeError=null;
   var timer=new Timer{Interval=30};
   timer.Tick+=(s,e)=> { foreach(Form dialog in Application.OpenForms.Cast<Form>().ToArray()) if(dialog!=f) {
    if(!All(dialog).Any(c=>c.Text=="Turn off the VPN before changing TUN settings.")) noticeError="wrong notice text";
    notices++; dialog.Close();
   }};
   timer.Start();
   int changes=0; AppLists.Changed+=k=>changes++;
   r.State=RouteState.Connecting;
   Call(f,"AddToList",AppLists.HybridApps,"blocked.exe");
   Call(f,"RemoveFromList",AppLists.HybridExcl,new List<string>{"game.exe"});
   Call(f,"RenameInList",AppLists.HybridExcl,"game.exe");
   Call(f,"PasteToList",AppLists.HybridApps,null);
   Call(f,"AddSelectedTasks",AppLists.HybridApps,false);
   Call(f,"AddToSrvList",AppLists.SrvApps,"blocked.exe");
   Call(f,"MoveBetweenLists",src.Box,dst,new List<string>{"blocked.exe"});
   Assert(notices==7 && noticeError==null,"seven themed blocking notices: "+notices+" "+noticeError);
   Assert(changes==0,"blocked actions emitted no list changes");
   Assert(AppLists.Get(AppLists.HybridExcl).Contains("game.exe"),"blocked delete preserved exclude");
   r.State=RouteState.Off; x.State=XState.Connected;
   Call(f,"AddToList",AppLists.HybridApps,"blocked-sub.exe");
   Assert(notices==8 && changes==0,"subscriptions block edits too");
   timer.Stop(); timer.Dispose();
   x.State=XState.Off;
   Call(f,"AddToList",AppLists.HybridApps,"after-disconnect.exe");
   Assert(AppLists.Get(AppLists.HybridApps).Contains("after-disconnect.exe"),"edits restored after disconnect");
   Console.WriteLine("PASS: "+checks+" assertions; "+notices+" themed blocking notices; no VPN processes started.");
   return 0;
  } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
 }
}
