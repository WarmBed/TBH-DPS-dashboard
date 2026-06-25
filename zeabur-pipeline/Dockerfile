FROM node:20-alpine
RUN apk add --no-cache git openssh-client
WORKDIR /app
COPY package.json ./
COPY service.mjs ./
CMD ["node", "service.mjs"]
