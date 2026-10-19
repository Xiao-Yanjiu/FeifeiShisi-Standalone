using NativeDesktop;
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
Check(!AntiIdle.Due(false,true,false,90000,200000,0),"disabled never sends");
Check(!AntiIdle.Due(true,false,false,90000,200000,0),"offline never sends");
Check(!AntiIdle.Due(true,true,false,90000,10000,120000),"rate limited");
Check(!AntiIdle.Due(true,true,true,59999,200000,0),"foreground active user untouched");
Check(AntiIdle.Due(true,true,true,60000,200000,0),"foreground idle candidate");
Check(AntiIdle.Due(true,true,false,0,200000,0),"background selected target candidate");
using (var idle = new AntiIdle(()=>null,_=>false)) Check(!idle.Enabled,"off by default");
Console.WriteLine($"{checks} anti-idle adapter checks passed; no game window received input.");
Console.WriteLine($"{OriginalDesktop.PhoneChatCore.CheckOriginalBusiness()} original business checks passed. Push requests were not sent.");
