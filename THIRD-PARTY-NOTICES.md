# Third-Party Notices

SQLMCPSharp is licensed under the MIT License (see `LICENSE`). It depends on the
third-party components listed below. Each remains under its own license; this
file is provided for attribution.

## NuGet packages

| Package | License |
| --- | --- |
| Microsoft.AspNetCore.App | MIT |
| Microsoft.Data.SqlClient | MIT |
| Microsoft.Data.Sqlite | MIT |
| Microsoft.Extensions.Hosting.WindowsServices | MIT |
| MySqlConnector | MIT |
| ModelContextProtocol.AspNetCore | Apache-2.0 |
| Serilog.AspNetCore | Apache-2.0 |
| Serilog.Enrichers.Environment | Apache-2.0 |
| Serilog.Enrichers.Process | Apache-2.0 |
| Serilog.Enrichers.Thread | Apache-2.0 |
| Serilog.Settings.Configuration | Apache-2.0 |
| Serilog.Sinks.Console | Apache-2.0 |
| Serilog.Sinks.File | Apache-2.0 |
| Oracle.ManagedDataAccess.Core | Oracle Free Distribution Terms |

The full text of the MIT and Apache-2.0 licenses is available at
<https://opensource.org/license/mit> and
<https://www.apache.org/licenses/LICENSE-2.0> respectively.

## Oracle Free Distribution, Hosting, and Use Terms

`Oracle.ManagedDataAccess.Core` (the official Oracle ADO.NET client) ships under
Oracle's "Free Distribution, Hosting, and Use Terms" rather than a public
open-source licence. Key points:

- You may develop, test, prototype, deploy, and redistribute the driver
  **unmodified** as part of your own application, including in hosted /
  cloud-service environments.
- You must retain Oracle's copyright and license notices; do not reverse-engineer
  the driver.
- The full text ships inside the NuGet package as `LICENSE.txt`; see it before
  redistribution.

Do not enable the Oracle engine (`Sql:Oracle:Enabled=true`) if these terms are
not acceptable for your deployment. The MSSQL, MySQL, and SQLite engines are
independent and remain under their own MIT licences.

## Trademarks

"Microsoft SQL Server", "MySQL", "MariaDB", "Oracle", and "SQLite" are
trademarks of their respective owners. Use of these names in this project does
not imply endorsement by, or affiliation with, those entities.
