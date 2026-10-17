import * as bleh from '@playwright/mcp';

async function main() {
  console.log('Starting...');
  let thing = await bleh.createConnection();
  console.log('Connection created:', thing);
}

main()
  .catch((err) => {
    console.error(err);
    process.exit(1);
  })
  .then(() => {
    process.exit(0);
  });
