#!/bin/bash
# Metal Toolchain 未安裝，shader 走 runtime 編譯，因此不需要 metal compiler
set -e
cd "$(dirname "$0")"
swiftc -O -swift-version 5 \
  -o wall42 \
  Sources/Config.swift Sources/ControlPanel.swift Sources/MenuBar.swift Sources/Renderer.swift Sources/main.swift \
  -framework Cocoa -framework MetalKit
echo "build ok -> $(pwd)/wall42"
