# The ApplPi OS build: pi-gen at a fixed release, with its stages 0 and 1 (a bare bootable system) and our own stage
# in place of stage 2 (the Lite image). Built for linux/386, as pi-gen's own build-docker.sh does on 64-bit hosts.
FROM i386/debian:trixie

ARG PIGEN_REF=2026-10-06-raspios-trixie-armhf

ENV DEBIAN_FRONTEND=noninteractive

# pi-gen's dependencies (its Dockerfile and depends file)
RUN apt-get -y update && \
    apt-get -y install --no-install-recommends \
        git parted quilt coreutils qemu-user debootstrap zerofree zip dosfstools e2fsprogs \
        libarchive-tools libcap2-bin rsync grep udev xz-utils curl xxd file kmod bc \
        ca-certificates fdisk gpg pigz arch-test \
    && rm -rf /var/lib/apt/lists/*

RUN git clone --depth 1 --branch "${PIGEN_REF}" https://github.com/RPi-Distro/pi-gen /pi-gen \
    && rm -rf /pi-gen/stage2 /pi-gen/stage3 /pi-gen/stage4 /pi-gen/stage5 \
    # The first-boot user rename: the image has a fixed admin user with no password, reached by SSH key only
    && rm -rf /pi-gen/export-image/01-user-rename

COPY config build-in-container.sh /pi-gen/
COPY stage2-applpi /pi-gen/stage2-applpi
RUN chmod +x /pi-gen/build-in-container.sh && find /pi-gen/stage2-applpi -name '*.sh' -exec chmod +x {} +

VOLUME [ "/pi-gen/work" ]
WORKDIR /pi-gen
