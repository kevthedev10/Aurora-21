These rules are copied unchanged from develop commit `8f736d0e`, whose latest
migration is 140. They are historical test inputs, not runtime defaults. The
fixture builds the embedded base schema and migrations 001–140 forward in an
empty, disposable PostgreSQL database, then stores these rule documents.

Only the three documents changed by migrations after 140 are frozen here. Other
rules are filled by the current runtime. Club data is synthetic. This models the
supported schema and affected rules; it is not a full dump of the VPS database.
Do not update these files when changing current defaults.
