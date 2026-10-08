- **An email field took a display name or header form such as `Name <a@b.co>`.** It now takes a
  bare address only: angle brackets, quotes, parentheses, commas, semicolons, colons, square
  brackets, backslashes and control or format characters are refused, and so is an address longer
  than 254 characters. This refuses some writes that used to be accepted. A value already stored is
  still read as it is, and an edit that sends it back unchanged is accepted.
