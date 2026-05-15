#!/usr/bin/env bash
# Boot the dockur-prepared Windows 11 disk directly with QEMU + PulseAudio.
# Forwards SSH (2222) and RDP (3389); VNC on display :0 (port 5900).
# Audio: reads from the host's PulseAudio "winpepper_mic.monitor" source so
# anything paplay'd into the winpepper_mic null-sink lands as Windows Line In.
#
# Prereqs (one-time host setup, see scripts/setup-audio-host.sh):
#   1. PulseAudio installed and running as user service
#   2. winpepper_mic null-sink loaded (creates winpepper_mic.monitor source)
#   3. User in the kvm group (or /dev/kvm chmod 0666)
#   4. /home/jesse/windows-vm/storage already populated by a prior dockur run
#
# Logs: /home/jesse/windows-vm/{qemu.log,qemu-serial.log}
# Monitor socket: /home/jesse/windows-vm/qemu-monitor.sock

set -euo pipefail
STORAGE=/home/jesse/windows-vm/storage
LOGDIR=/home/jesse/windows-vm
PULSE_SOCKET=/run/user/$(id -u)/pulse/native

if [ ! -S "$PULSE_SOCKET" ]; then
    echo "PulseAudio socket not found at $PULSE_SOCKET. Start it with:" >&2
    echo "  systemctl --user start pulseaudio.service" >&2
    exit 1
fi

exec qemu-system-x86_64 \
    -name winpepper-vm \
    -enable-kvm \
    -machine q35,accel=kvm,smm=off \
    -cpu host,hv_passthrough,migratable=no \
    -smp 4,sockets=1,cores=4,threads=1 \
    -m 8G \
    -rtc base=localtime \
    -nodefaults \
    -global ICH9-LPC.disable_s3=1 \
    -global ICH9-LPC.disable_s4=1 \
    \
    -drive file="$STORAGE/windows.rom",if=pflash,format=raw,readonly=on,unit=0 \
    -drive file="$STORAGE/windows.vars",if=pflash,format=raw,unit=1 \
    \
    -object iothread,id=io2 \
    -drive file="$STORAGE/data.img",id=data0,format=raw,cache=none,aio=native,discard=on,detect-zeroes=on,if=none \
    -device virtio-scsi-pci,id=scsi0,bus=pcie.0,addr=0xa,iothread=io2 \
    -device scsi-hd,drive=data0,bus=scsi0.0,channel=0,scsi-id=0,lun=0,bootindex=1 \
    \
    -netdev user,id=net0,hostfwd=tcp:127.0.0.1:2222-:22,hostfwd=tcp:127.0.0.1:3389-:3389 \
    -device virtio-net-pci,netdev=net0,mac=02:E2:13:DE:47:EC \
    \
    -audiodev pa,id=pa1,server=unix:${PULSE_SOCKET},in.name=winpepper_mic.monitor \
    -device intel-hda \
    -device hda-duplex,audiodev=pa1 \
    \
    -display none \
    -vnc 127.0.0.1:0 \
    -vga virtio \
    -monitor unix:"$LOGDIR/qemu-monitor.sock",server,nowait \
    -serial file:"$LOGDIR/qemu-serial.log" \
    \
    -device qemu-xhci,id=xhci \
    -device usb-tablet \
    -object rng-random,id=rng0,filename=/dev/urandom \
    -device virtio-rng-pci,rng=rng0 \
    \
    -pidfile "$LOGDIR/qemu.pid" \
    -D "$LOGDIR/qemu.log"
