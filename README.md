# North State Liquidators

Website for **North State Liquidators** — Raleigh, NC wholesale + retail liquidation.
Sources overstock, shelf-pulls, and customer-return merchandise from Amazon and big-box retailers.

- **Live site:** https://northstateliquidators.com
- **Owners:** Norman Turner & Rob TeCarr
- **Phone:** (919) 526-0112

## Hosting

Static site served via **GitHub Pages** from `main` branch root. Domain registered at GoDaddy, pointed at GitHub Pages via A + CNAME records.

## Structure

```
index.html                              Main landing page
north_state_liquidators_logo.jpeg       Primary logo
CNAME                                   GitHub Pages custom-domain binding
mockups/                                Design iterations
PROJECT.md                              Internal project tracker
```

## Development

Plain HTML/CSS — no build step. Edit `index.html`, commit, push. Changes go live in ~1 minute via GitHub Pages.

```bash
git clone https://github.com/Brown-Dog-Soup/northstateliquidators.git
# edit index.html
git commit -am "update"
git push
```

## Project context

See [`PROJECT.md`](PROJECT.md) for full scope, audience breakdown, branding, and open questions.
