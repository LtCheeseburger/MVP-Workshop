#include "kernel/mods.h"
#include "core/log.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <cstdarg>
#include <thread>
#include <atomic>
#include <chrono>
namespace fs = std::filesystem;
namespace logx { void Write(Level, const char* format, ...) { va_list args; va_start(args, format); vprintf(format, args); puts(""); va_end(args); } }
void Write(const fs::path& path, const std::string& text) { fs::create_directories(path.parent_path()); std::ofstream(path, std::ios::binary) << text; }
std::string Read(const fs::path& path) { std::ifstream in(path, std::ios::binary); return std::string(std::istreambuf_iterator<char>(in), {}); }
int main(int argc, char** argv) {
    if (argc != 2) return 2;
    auto root = fs::absolute(argv[1]); auto game = root / "game", publish = root / "published", request = publish / "reload.json";
    int failed = 0; auto check = [&](const char* label, bool value) { std::cout << (value ? "PASS " : "FAIL ") << label << '\n'; if (!value) failed++; };
    auto manifest = std::string("{\"format\":1,\"layout\":1,\"id\":\"workshop-test\",\"version\":\"1.0.0\",\"game\":{\"xbeSha1\":\"fake\"}}");
    auto generation = [&](char id, const std::string& value) { std::string revision(32, id); auto dir = publish / "generations" / revision; Write(dir / "mod.json", manifest); if (!value.empty()) Write(dir / "files/new.txt", value); return dir; };
    auto pointer = [&](char id) { std::string revision(32,id); Write(request, "{\"format\":1,\"revision\":\"" + revision + "\",\"mod\":\"generations/" + revision + "\"}"); kernel::mods::PollReload(); return Read(publish / "reload.ack.json"); };
    Write(game / "default.xbe", "fake game bytes"); auto initial = generation('0', "old");
    kernel::mods::Init(game, root / "no-installed-mods", {initial}, true, "fake"); kernel::mods::ConfigureReload(request, initial, "fake");
    check("initial revision is acknowledged without reloading guest resources", pointer('0').find("\"applied\"") != std::string::npos);
    generation('1', "updated"); check("unopened replacement is applied", pointer('1').find("\"applied\"") != std::string::npos);
    std::vector<uint8_t> bytes; kernel::mods::Load(game / "new.txt", bytes); check("VFS reads new generation bytes", std::string(bytes.begin(), bytes.end()) == "updated");
    generation('2', "unsafe loaded edit"); check("opened asset requires restart", pointer('2').find("\"restart-required\"") != std::string::npos);
    bytes.clear(); kernel::mods::Load(game / "new.txt", bytes); check("restart-required leaves all active bytes unchanged", std::string(bytes.begin(),bytes.end()) == "updated");
    auto bad = generation('3', "invalid"); Write(bad / "mod.json", "{\"format\":99}"); check("invalid mod is rejected without changing mount", pointer('3').find("\"rejected\"") != std::string::npos);
    Write(request, "{\"format\":1,\"revision\":\"44444444444444444444444444444444\",\"mod\":\"../escape\"}"); kernel::mods::PollReload(); check("path traversal is rejected", Read(publish / "reload.ack.json").find("\"rejected\"") != std::string::npos);
    auto next = generation('5', "updated"); Write(next / "files/unopened.txt", "new asset"); check("unrelated unopened file can reload after an earlier refusal", pointer('5').find("\"applied\"") != std::string::npos);
    generation('6', "updated"); check("reverting an unopened addition removes it", pointer('6').find("\"applied\"") != std::string::npos && !kernel::mods::Exists(game / "unopened.txt"));
    auto next2 = generation('7', "updated"); Write(next2 / "files/newpack_big/asset.txt", "future asset"); kernel::mods::RecordOpen(game / "newpack_big");
    check("opened containing archive blocks member changes", pointer('7').find("\"restart-required\"") != std::string::npos);
    generation('8', "updated");
    std::atomic<bool> completed = false; std::thread worker;
    { auto lock = kernel::mods::Lock(); worker = std::thread([&] { pointer('8'); completed = true; }); std::this_thread::sleep_for(std::chrono::milliseconds(50)); check("mount publication waits for complete VFS transactions", !completed.load()); }
    worker.join(); check("publication completes after VFS transaction releases", completed.load());
    kernel::mods::List(game);
    auto next3 = generation('9', "updated"); Write(next3 / "files/after-listing.txt", "new child");
    check("cached directory listings block topology changes", pointer('9').find("\"restart-required\"") != std::string::npos);
    check("game source was never written", Read(game / "default.xbe") == "fake game bytes");
    return failed ? 1 : 0;
}
