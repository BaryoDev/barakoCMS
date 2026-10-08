- **A public form submission read only the first spelling of a field sent twice.**
  `POST /api/public/forms/{slug}` now refuses a field sent under more than one spelling, ignoring
  case, with a 400 on that field, as a signed-in write does. BarakoCMS.Forms goes to 4.4.1.
