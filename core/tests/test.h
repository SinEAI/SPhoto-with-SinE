// Minimal test harness: no third-party test framework for the core.
#pragma once

#include <cstdio>
#include <functional>
#include <string>
#include <vector>

namespace sptest {

struct Case {
  const char* name;
  std::function<void()> body;
};

std::vector<Case>& registry();
void fail(const char* file, int line, const std::string& message);

struct Register {
  Register(const char* name, std::function<void()> body) {
    registry().push_back({name, std::move(body)});
  }
};

// Thrown by a test that cannot run here (for example, no GPU).
struct Skip {
  std::string reason;
};

}  // namespace sptest

#define SP_CONCAT_(a, b) a##b
#define SP_CONCAT(a, b) SP_CONCAT_(a, b)
#define TEST(name)                                                   \
  static void name();                                                \
  static sptest::Register SP_CONCAT(reg_, name)(#name, name);        \
  static void name()

#define EXPECT(cond)                                                 \
  do {                                                               \
    if (!(cond)) sptest::fail(__FILE__, __LINE__, "EXPECT(" #cond ")"); \
  } while (0)
