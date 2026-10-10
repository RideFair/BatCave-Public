# Monitor arrangement

BatCave on Windows shows the actual Windows monitor arrangement. Select the
monitor to cross from, then drag the other computer to the right, left, above,
or below it. Optional Position controls expose the side and alignment controls.
The green edge shows the portion that will transfer input.

Only exposed edges can transfer input. A boundary occupied by another Windows
monitor stays local. An invalid or missing monitor disables sharing until you
select a usable edge. Editing or saving pauses sharing; starting it again is
always explicit. Display changes disconnect and pause the session.

Link devices saves the arrangement when leaving Arrange screens. Save setup also
stores it for the selected approved computer. Offline arrangement uses a labeled placeholder;
the authenticated peer's real size is required before input can start. A peer
currently appears as one aggregate desktop, even when it has multiple monitors.
Individual remote monitor selection requires a future negotiated hello extension.

The saved monitor identifier tracks a Windows display slot, without publishing
its native name or hardware serial. Reconnecting a display to a different slot
can require selecting it again. The original development layout is untouched.

Connection details shows the Windows network addresses, encrypted port and full
pairing-card fingerprint for manual setup on the other computer. These details
stay on the local machine and are not repository configuration.

## Geometry contract

Generated `batcave.topology.v1` profiles use inclusive bounds: right is
`left + width - 1`, and bottom is `top + height - 1`. A normalized position of
one maps to the last pixel. Coordinates belong to the originating desktop;
receivers resolve normalized target positions in their own native space.

The v0 aggregate peer hello remains compatible. Its redundant endpoint fields
may use inclusive pixels or the earlier Mac exclusive CGRect maximum. Routing
derives inclusive bounds from origin and size and rejects inconsistent values.
No new monitor array or file-transfer capability is emitted by this app.

The routing tests cover both directions, four sides, alignment extremes, negative
origins, unequal resolutions, local edge obstruction, persistence, and malformed
profiles. They do not prove native cross-platform DPI behavior or live dragging.
