#include <cstdio>

#include "test.h"

namespace sptest {

namespace {
int g_failures = 0;
}

std::vector<Case>& registry() {
  static std::vector<Case> cases;
  return cases;
}

void fail(const char* file, int line, const std::string& message) {
  ++g_failures;
  std::printf("  FAILED %s:%d %s\n", file, line, message.c_str());
}

}  // namespace sptest

int main() {
  int failed_cases = 0;
  for (const auto& test : sptest::registry()) {
    const int before = sptest::g_failures;
    try {
      test.body();
    } catch (const sptest::Skip& skip) {
      std::printf("SKIP %s: %s\n", test.name, skip.reason.c_str());
      continue;
    }
    const bool ok = sptest::g_failures == before;
    if (!ok) ++failed_cases;
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", test.name);
  }
  std::printf("%zu tests, %d failed\n", sptest::registry().size(), failed_cases);
  return failed_cases == 0 ? 0 : 1;
}
