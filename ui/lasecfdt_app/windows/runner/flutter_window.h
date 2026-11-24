#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

#include <flutter/dart_project.h>
#include <flutter/encodable_value.h>
#include <flutter/flutter_view_controller.h>
#include <flutter/method_channel.h>

#include <map>
#include <memory>

#include "win32_window.h"

// Janela que hospeda a UI Flutter do LasecFDT e mantém "encaixadas" as janelas das telas dos DTMs.
//
// As telas dos DTMs são controles ActiveX que vivem nos processos worker (um por instância de DTM). Elas não
// viram filhas desta janela (SetParent entre processos acopla as filas de entrada: um DTM travado travaria a UI).
// São janelas sem moldura cuja dona é esta janela, posicionadas sobre a área de conteúdo que o Dart reserva para
// elas, com SetWindowPos assíncrono (SWP_ASYNCWINDOWPOS: nunca espera o processo do DTM).
class FlutterWindow : public Win32Window {
 public:
  explicit FlutterWindow(const flutter::DartProject& project);
  virtual ~FlutterWindow();

 protected:
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  struct Docked {
    double x = 0, y = 0, w = 0, h = 0;  // pixels lógicos, relativos à view do Flutter
    bool visible = false;
  };

  void HandleNative(const flutter::MethodCall<flutter::EncodableValue>& call,
                    std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result);
  void Place(HWND target, const Docked& d);
  void PlaceAll();

  flutter::DartProject project_;
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> native_channel_;
  std::map<HWND, Docked> docked_;
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
