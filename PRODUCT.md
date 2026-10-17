# Product

## Register

product

## Platform

windows

## Design Language

Native Windows 11 desktop UI with WinUI 3 and Microsoft Fluent Design. WinWispr
is not a web surface and should not use browser-specific live-mode assumptions.

## Users

People who want to dictate text quickly into the application they are using
on Windows 11. They use WinWispr through a global hotkey in their daily work
and expect their speech to appear directly in the active window.

## Product Purpose

WinWispr is a local Windows dictation assistant. It records speech through a
global hotkey, recognizes it locally with Parakeet, and pastes the result into
the active Windows window or copies it to the clipboard.

The core is deliberately small: press the hotkey, speak, stop, and receive
text. There is no cloud processing, account, history, or general-purpose AI
assistant. Success means dictation reaches the target application quickly,
reliably, and transparently, without making users uneasy about data security.

## Brand Personality

Private, fast, and simple. WinWispr should feel calm, direct, and trustworthy.
The interface explains only what the current task requires and makes local
processing and control over data easy to understand.

## Anti-references

WinWispr should not look like a cloud AI service, an overloaded product hub,
or a surveillance tool. Avoid unclear data flows, intrusive permission or
upsell patterns, unnecessary accounts, and statistics or history components
that undermine the dictation assistant's private nature.

## Design Principles

- **Local by default:** Data processing, audio, and transcription stay local;
  network access is limited to model downloads explicitly started by the user
  and an anonymous update check that users and administrators can turn off;
  an update is downloaded and installed only after the user clicks the offer.
- **Fast path to text:** Keep the path from hotkey to pasted text short,
  visible, and free of unnecessary dialogs.
- **Calm confidence:** Explain states, errors, and data handling clearly and
  factually, without alarmism or marketing pressure.
- **Small surface, complete control:** The overlay, tray, and Settings cover
  the dictation workflow precisely; the hotkey, microphone, model, and startup
  behavior remain understandable and controllable.
- **Native Windows fit:** Interaction, theming, keyboard accessibility, and
  UI Automation follow Windows 11 desktop application conventions.

## Accessibility & Inclusion

WinWispr follows the Windows theme, supports keyboard operation, and provides
meaningful UI Automation names. States and actions must not depend solely on
color or motion. Avoid additional decorative motion; existing animations must
not impair usability when reduced motion is enabled.
