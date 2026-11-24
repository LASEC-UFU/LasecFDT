#include "flutter_window.h"

#include <flutter/standard_method_codec.h>

#include <cmath>
#include <optional>

#include "flutter/generated_plugin_registrant.h"

namespace {

using flutter::EncodableMap;
using flutter::EncodableValue;

int64_t ArgInt(const EncodableMap& m, const char* key) {
  auto it = m.find(EncodableValue(key));
  if (it == m.end()) return 0;
  if (auto v = std::get_if<int32_t>(&it->second)) return *v;
  if (auto v = std::get_if<int64_t>(&it->second)) return *v;
  if (auto v = std::get_if<double>(&it->second)) return static_cast<int64_t>(*v);
  return 0;
}

double ArgDouble(const EncodableMap& m, const char* key) {
  auto it = m.find(EncodableValue(key));
  if (it == m.end()) return 0;
  if (auto v = std::get_if<double>(&it->second)) return *v;
  if (auto v = std::get_if<int32_t>(&it->second)) return *v;
  if (auto v = std::get_if<int64_t>(&it->second)) return static_cast<double>(*v);
  return 0;
}

bool ArgBool(const EncodableMap& m, const char* key) {
  auto it = m.find(EncodableValue(key));
  if (it == m.end()) return false;
  if (auto v = std::get_if<bool>(&it->second)) return *v;
  return false;
}

}  // namespace

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  native_channel_ = std::make_unique<flutter::MethodChannel<EncodableValue>>(
      flutter_controller_->engine()->messenger(), "lasecfdt/native",
      &flutter::StandardMethodCodec::GetInstance());
  native_channel_->SetMethodCallHandler(
      [this](const auto& call, auto result) { HandleNative(call, std::move(result)); });

  // Abre maximizada (thin clients: a aplicação ocupa a tela da estação).
  flutter_controller_->engine()->SetNextFrameCallback([&]() { ::ShowWindow(GetHandle(), SW_SHOWMAXIMIZED); });
  flutter_controller_->ForceRedraw();
  return true;
}

void FlutterWindow::OnDestroy() {
  // As telas dos DTMs são donas desta janela: escondê-las antes de destruir evita que fiquem órfãs na tela
  // enquanto o Host libera os workers pelo protocolo.
  for (auto& kv : docked_) ::ShowWindowAsync(kv.first, SW_HIDE);
  docked_.clear();
  native_channel_ = nullptr;
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }
  Win32Window::OnDestroy();
}

void FlutterWindow::HandleNative(
    const flutter::MethodCall<EncodableValue>& call,
    std::unique_ptr<flutter::MethodResult<EncodableValue>> result) {
  const std::string& method = call.method_name();
  const auto* args = std::get_if<EncodableMap>(call.arguments());
  EncodableMap empty;
  const EncodableMap& a = args ? *args : empty;

  if (method == "hwnd") {
    result->Success(EncodableValue(static_cast<int64_t>(reinterpret_cast<intptr_t>(GetHandle()))));
    return;
  }
  if (method == "dock") {
    // dock {hwnd, x, y, w, h, visible}: posição em pixels lógicos dentro da view do Flutter.
    HWND target = reinterpret_cast<HWND>(static_cast<intptr_t>(ArgInt(a, "hwnd")));
    if (!::IsWindow(target)) {
      result->Success(EncodableValue(false));
      return;
    }
    Docked d;
    d.x = ArgDouble(a, "x"); d.y = ArgDouble(a, "y"); d.w = ArgDouble(a, "w"); d.h = ArgDouble(a, "h");
    d.visible = ArgBool(a, "visible");
    docked_[target] = d;
    Place(target, d);
    result->Success(EncodableValue(true));
    return;
  }
  if (method == "undock") {
    HWND target = reinterpret_cast<HWND>(static_cast<intptr_t>(ArgInt(a, "hwnd")));
    auto it = docked_.find(target);
    if (it != docked_.end()) {
      ::ShowWindowAsync(target, SW_HIDE);
      docked_.erase(it);
    }
    result->Success(EncodableValue(true));
    return;
  }
  if (method == "hideAll") {
    // Diálogos e menus do Flutter são desenhados na própria janela, por baixo das telas dos DTMs:
    // enquanto estão abertos, as telas são escondidas (o estado delas não se perde).
    bool hide = ArgBool(a, "hide");
    for (auto& kv : docked_) {
      if (!::IsWindow(kv.first)) continue;
      if (hide) ::ShowWindowAsync(kv.first, SW_HIDE);
      else if (kv.second.visible) Place(kv.first, kv.second);
    }
    result->Success(EncodableValue(true));
    return;
  }
  if (method == "activate") {
    ::ShowWindow(GetHandle(), SW_RESTORE);
    ::SetForegroundWindow(GetHandle());
    result->Success(EncodableValue(true));
    return;
  }
  result->NotImplemented();
}

void FlutterWindow::Place(HWND target, const Docked& d) {
  if (!flutter_controller_ || !::IsWindow(target)) return;
  if (!d.visible || d.w < 1 || d.h < 1 || ::IsIconic(GetHandle())) {
    ::ShowWindowAsync(target, SW_HIDE);
    return;
  }
  HWND view = flutter_controller_->view()->GetNativeWindow();
  double scale = ::GetDpiForWindow(view) / 96.0;
  POINT origin{static_cast<LONG>(std::lround(d.x * scale)), static_cast<LONG>(std::lround(d.y * scale))};
  ::ClientToScreen(view, &origin);
  int w = static_cast<int>(std::lround(d.w * scale));
  int h = static_cast<int>(std::lround(d.h * scale));
  ::SetWindowPos(target, nullptr, origin.x, origin.y, w, h,
                 SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);
}

void FlutterWindow::PlaceAll() {
  for (auto it = docked_.begin(); it != docked_.end();) {
    if (!::IsWindow(it->first)) {
      it = docked_.erase(it);  // o worker fechou a tela ou caiu
      continue;
    }
    Place(it->first, it->second);
    ++it;
  }
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
    case WM_MOVE:
    case WM_WINDOWPOSCHANGED:
      // Arrastar/maximizar a janela: as telas dos DTMs acompanham (o redimensionamento da área de conteúdo
      // chega pelo Dart, que reenvia "dock" quando o layout muda).
      PlaceAll();
      break;
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}
