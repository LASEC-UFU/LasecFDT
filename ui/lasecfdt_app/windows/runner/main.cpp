#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include "flutter_window.h"
#include "utils.h"

namespace {
constexpr const wchar_t kTitle[] = L"LasecFDT";
// "Local\" = um objeto por SESSÃO do Windows: em servidor de terminal cada estação (thin client) tem a sua
// instância, e a mesma estação não abre duas (dois Hosts disputariam o mesmo projeto e as mesmas portas).
constexpr const wchar_t kSingleInstanceMutex[] = L"Local\\LasecFDT.UI.SingleInstance";
}  // namespace

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  HANDLE single = ::CreateMutexW(nullptr, TRUE, kSingleInstanceMutex);
  if (single != nullptr && ::GetLastError() == ERROR_ALREADY_EXISTS) {
    // Já aberta nesta sessão: traz a janela existente para a frente.
    HWND existing = ::FindWindowW(L"FLUTTER_RUNNER_WIN32_WINDOW", kTitle);
    if (existing) {
      if (::IsIconic(existing)) ::ShowWindow(existing, SW_RESTORE);
      ::SetForegroundWindow(existing);
    }
    ::CloseHandle(single);
    return EXIT_SUCCESS;
  }

  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

  flutter::DartProject project(L"data");
  std::vector<std::string> command_line_arguments = GetCommandLineArguments();
  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project);
  Win32Window::Point origin(40, 40);
  Win32Window::Size size(1440, 900);
  if (!window.Create(kTitle, origin, size)) {
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  ::CoUninitialize();
  if (single) ::CloseHandle(single);
  return EXIT_SUCCESS;
}
