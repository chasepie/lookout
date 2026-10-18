# Lookout Design Proposal

Lookout is a web application that allows users to define web scrapers with the intention of detecting changes for a given webpage - for example: determining when a product is back in stock, a price has changed, etc.

- Web Scraping
  - Users can define a scraper by writing a custom node.js (typescript or javascript) script that will be executed by the backend
  - Future scraper options will include (but discused in future proposals):
    - A GUI-based scraper builder via the frontend for less-complex scraping scenarios
    - An AI Agent that will accept a prompt and perform the scraping using the Playwright MCP server
  - To avoid arbitrary code execution, the backend will execute the web scrapers in a sandboxed environment, and will only allow access to Node.js built-in modules and playwright
  - Browserless will be used to execute the web scrapers in a containerized environment, but other options will be considered if Browserless is not a good fit
- Backend
  - Build using .NET 10
  - Expose a REST API for the frontend to consume
  - Execute user-defined web scrapers in a sandboxed environment
  - Secured by single user account using either JWT or cookie-based authentication
  - Store results of web scrapers in a database using EF Core
  - Database will be a SQLite database, but other options will be considered if this is not a good fit
  - Send notifications to users when a change is detected using Discord or Pushover - if configured by the user
  - Will send real-time updates to the frontend using WebSockets (SignalR preferred, but other options will be considered if SignalR is not a good fit)
  - Deployed via Docker containers
  - Use PrivateInternetAccess VPN to allow webscrapers to access websites over VPN
- Frontend
  - Build using Angular 22 and TypeScript
  - Single Page Application (SPA)
  - Allow users to define web scrapers, view the results of the web scrapers, and receive notifications when a change is detected
  - Allow users to upload their own custom node.js (typescript or javascript) script that will be executed by the backend
  - Communicate with the backend via the REST API
  - Allow for real-time updates of the results of the web scrapers using WebSockets (SignalR preferred, but other options will be considered if SignalR is not a good fit)
