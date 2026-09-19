# UN_Nexo website

This directory contains the official UN_Nexo landing site intended for Cloudflare Workers Static Assets.

## Cloudflare Git deployment

In Cloudflare **Workers & Pages → Create application → Connect GitHub**:

- Repository: \`yannickfan67-ai/UN_Nexo\`
- Worker name: \`un-nexo\`
- Production branch: \`main\`
- Root directory: \`website\`
- Build command: leave empty
- Deploy command: \`npx wrangler deploy\`

The Wrangler configuration serves everything in \`website/public/\` as static assets.

For local preview:

\`\`\`bash
cd website
npx wrangler dev
\`\`\`
