#!/bin/bash -e
# Compiled libraries for apps (apps are plain Python; what needs compiling comes with the image), from wheels built
# for Raspberry Pi OS by piwheels, each pinned to its file's SHA-256

# rpi_ws281x: WS2812 (NeoPixel) LEDs on GPIO 18 by PWM and DMA, as on the Unicorn HAT. It needs root (/dev/mem), and
# the PWM hardware to itself: the onboard (headphone) audio uses it too, so an app that says "pwm" in its manifest
# has the audio driver unloaded while it runs (see app).
WHEELS=(
	"rpi_ws281x-5.0.0-cp313-cp313-linux_armv7l.whl ede81f788319a26d7f46591266f20bf3f83cf662eadd402d74d926a426c80e06"
)

# The wheels are for the image's Python
[ -x "${ROOTFS_DIR}/usr/bin/python3.13" ] || { echo "The wheels are for Python 3.13, which the image doesn't have" >&2; exit 1; }
site="${ROOTFS_DIR}/usr/local/lib/python3.13/dist-packages"
install -d "$site"
for entry in "${WHEELS[@]}"; do
	read -r wheel sha256 <<< "$entry"
	project=${wheel%%-*}
	curl -sSfL --retry 3 -o "/tmp/${wheel}" "https://www.piwheels.org/simple/${project//_/-}/${wheel}"
	echo "${sha256}  /tmp/${wheel}" | sha256sum -c -
	bsdtar -xf "/tmp/${wheel}" -C "$site"
	rm "/tmp/${wheel}"
done
