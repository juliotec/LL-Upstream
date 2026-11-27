# A launcher service may kill its entire control group when its window closes.
# Register this PID before exec so the builder and its descendants share the scope.
if [ -S "${XDG_RUNTIME_DIR:-}/systemd/private" ] && command -v busctl >/dev/null 2>&1; then
    if busctl --user --timeout=3s call org.freedesktop.systemd1 /org/freedesktop/systemd1 \
        org.freedesktop.systemd1.Manager StartTransientUnit 'ssa(sv)a(sa(sv))' \
        "longshot-play-$$.scope" fail 1 PIDs au 1 "$$" 0 >/dev/null 2>&1; then
        # StartTransientUnit returns a job before systemd has moved the PID.
        # Wait before the builder can fork children in the old control group.
        for attempt in 1 2 3 4 5 6 7 8 9 10; do
            grep -Fq "/longshot-play-$$.scope" "/proc/$$/cgroup" && break
            sleep .02
        done
    fi
fi
exec "$@"
