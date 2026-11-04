#!/usr/bin/env bash
# Builds the Acrux Preview plugin against a local Rider install (no Gradle/marketplace needed).
# Install: Rider > Settings > Plugins > (gear) > Install Plugin from Disk... > build/acrux-preview.jar
# Usage: [PLUGIN_VERSION=x] build.sh [rider-install-dir]   (default: $RIDER_HOME or /opt/rider)
set -euo pipefail
cd "$(dirname "$0")"
RIDER="${1:-${RIDER_HOME:-/opt/rider}}"
[ -d "$RIDER/lib" ] || { echo "Rider not found at $RIDER (pass its install folder)"; exit 1; }
# Compile with Rider's own runtime version (its jars target it): Rider's bundled JBR, else a matching JDK.
JAVAC="${JAVAC:-}"
if [ -z "$JAVAC" ]; then
  for candidate in "$RIDER/jbr/bin/javac" /usr/lib/jvm/java-25-openjdk/bin/javac "$(command -v javac)"; do
    [ -x "$candidate" ] && { JAVAC="$candidate"; break; }
  done
fi
RELEASE="$("$RIDER/jbr/bin/java" -XshowSettings:properties -version 2>&1 | sed -n 's/^ *java.specification.version = //p')"
JAR="$(dirname "$JAVAC")/jar"
[ -x "$JAR" ] || JAR="$(command -v jar)"
rm -rf build && mkdir -p build/classes
"$JAVAC" --release "${RELEASE:-25}" -nowarn -encoding UTF-8 -d build/classes -cp "$RIDER/lib/*" $(find src -name '*.java')
cp -r resources/* build/classes/
# PLUGIN_VERSION: the version the Acrux.Sdk build stamps on the jar it ships (resources/ keeps the development one).
if [ -n "${PLUGIN_VERSION:-}" ]; then
  sed -i.bak "s|<version>[^<]*</version>|<version>$PLUGIN_VERSION</version>|" build/classes/META-INF/plugin.xml
  rm -f build/classes/META-INF/plugin.xml.bak
fi
cp THIRD-PARTY-NOTICES.md build/classes/META-INF/
(cd build/classes && "$JAR" cf ../acrux-preview.jar .)
echo "built $(pwd)/build/acrux-preview.jar"
