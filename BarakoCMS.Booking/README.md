# BarakoCMS.Booking

Resources with opening hours and time zones, bookings that cannot overlap, and a public availability route.

```sh
dotnet add package BarakoCMS.Booking
```

The package reference plus a restart is the install. `BarakoCMS:Modules:Enabled` decides which
modules run (`BarakoCMS__Modules__Enabled=Booking`). To name it by hand, put
`modules.Add(new BarakoCMS.Booking.BookingModule())` in the `AddBarakoCMS` callback. See `MODULES.md`
in the repository.

See [docs/booking.md](../docs/booking.md).
