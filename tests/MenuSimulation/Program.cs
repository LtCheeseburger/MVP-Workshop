using System.Text;
using MVPWorkshop.Core.Formats;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
var session = new MenuSimulation();
session.Reset("main"); session.SetControls([new("play", 0, 0), new("options", 0, 20), new("disabled", 0, 10, false)]);
session.Move(0, 1); Check(session.Focus == "options", "direction navigation excludes disabled controls");
session.Routes.Add(new("main", "options", "options-screen", false)); Check(session.Activate(), "explicit route activation");
session.SetControls([new("settings", 0, 0)]); session.Open("confirm", true); session.SetControls([new("yes", 0, 0)]);
Check(session.IsPopup, "modal popup stack"); session.Back(); session.SetControls([new("settings", 0, 0)]);
Check(session.Screen == "options-screen" && session.Focus == "settings", "popup return focus");
session.Back(); session.SetControls([new("play", 0, 0), new("options", 0, 20)]);
Check(session.Focus == "options", "connected flow restores original focus");
session.Reset("main"); Check(!session.Back() && session.Focus is null, "reset clears history and focus");
for (int i = 0; i < 1100; i++) session.Log("test", "bounded"); Check(session.Trace.Count == 1000, "trace bounded");

// A minimal relocated APT fixture with an authored this.onRelease function.
byte[] bytes = new byte[1024];
void U(int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
Encoding.ASCII.GetBytes("Apt Data:6\x1a").CopyTo(bytes, 0);
U(28, 9); U(32, AptFile.Signature); U(56, 640); U(60, 480); U(64, 33);
Encoding.ASCII.GetBytes("onRelease\0").CopyTo(bytes, 700);
Encoding.ASCII.GetBytes("text\0").CopyTo(bytes, 720);
int pc = 128;
void Op(byte op) => bytes[pc++] = op;
void Pointer(byte op, int pointer) { Op(op); pc = (pc + 3) & ~3; U(pc, (uint)pointer); pc += 4; }
Op(0x70); Pointer(0xA1, 700); // this, "onRelease"
Op(0x9B); pc = (pc + 3) & ~3; int header = pc; pc += 24; int body = pc;
Op(0x70); Pointer(0xA1, 720); Op(0x5A); Op(0x4F); Op(0); // this.text = 1
U(header, 760); U(header + 12, (uint)(pc - body)); Op(0x4F); Op(0); // assign handler
pc = 256; Op(0x70); Pointer(0xA1, 720); Op(0xB5); Op(2); Op(0x4F); Op(0x07); Op(0);
var movie = AptFile.Read(bytes, 28);
var clip = new AptCharacter { Type = AptCharacterType.Sprite };
var frame = new AptFrame { Index = 0 }; frame.Items.Add(new() { Type = 1, Code = 128 }); clip.Frames.Add(frame);
var secondFrame = new AptFrame { Index = 1 }; secondFrame.Items.Add(new() { Type = 1, Code = 256 }); clip.Frames.Add(secondFrame);
var vm = new AptPreviewScript { Interactive = true };
vm.Initialize(movie, null, movie.Movie, 0); vm.EnterSprite("button", movie, null, clip, 0);
Check(vm.HasHandler("button", "onRelease"), "native handler discovered from authored bytecode");
Check(vm.Dispatch("button", "onRelease") && vm.Number("button", "text", 0) == 1, "native activation executes existing VM with correct this");
vm.SetPreviewValue("button", "text", 42d); vm.EnterSprite("button", movie, null, clip, 0);
Check(vm.Number("button", "text", 0) == 42, "rerender preserves runtime state");
Check(!vm.Dispatch("button", "missing"), "missing native handler never invokes simulated services");
Check(vm.EnterSprite("button", movie, null, clip, 1) == 1 && vm.Number("button", "text", 0) == 2, "supported timeline action advances");
Check(vm.EnterSprite("button", movie, null, clip, 0) == 1, "native stop holds timeline frame");
vm.Enabled = false; Check(!vm.Dispatch("button", "onRelease"), "scripts off prevents activation");
Console.WriteLine($"{checks} checks passed");

