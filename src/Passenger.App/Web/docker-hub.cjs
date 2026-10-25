// Passenger's preload for the Dockerfile language server (node --require, Web/DockerHubTags.cs): it changes the copy
// of dockerfile-language-service's DockerRegistryClient the server loads, which asks Docker Hub for an image's tags, as
// PASSENGER_DOCKER_HUB says:
//   off             getTags answers no tags and asks nothing (Settings' Complete image tags from Docker Hub is off);
//   an http(s) URL  each request goes there instead, its host and path under it (<URL>/auth.docker.io/token?...);
//   unset           nothing changes: Docker Hub is asked.
// A client it cannot find or change ends the server as it starts, so a server whose client has moved never asks Docker
// Hub while Passenger has said off.
"use strict";

const fs = require("fs");
const http = require("http");
const https = require("https");
const path = require("path");

const said = process.env.PASSENGER_DOCKER_HUB;

if (said) {
    // The copy the server's own require finds, its file read by path, which a package's exports field never hides.
    const server = path.dirname(fs.realpathSync(process.argv[1]));
    const service = path.dirname(require.resolve("dockerfile-language-service", { paths: [server] }));
    const { DockerRegistryClient } = require(path.join(service, "dockerRegistryClient.js"));
    if (typeof DockerRegistryClient !== "function" || typeof DockerRegistryClient.prototype.getTags !== "function"
        || typeof DockerRegistryClient.prototype.performHttpsGet !== "function") {
        throw new Error(`Passenger cannot govern the Dockerfile server's Docker Hub client in ${service}`);
    }
    if (said === "off") {
        DockerRegistryClient.prototype.getTags = function () {
            return Promise.resolve([]);
        };
    } else {
        const place = new URL(said);
        if (place.protocol !== "http:" && place.protocol !== "https:") {
            throw new Error(`PASSENGER_DOCKER_HUB takes off or an http or https URL, not ${said}`);
        }
        const under = place.pathname.endsWith("/") ? place.pathname : place.pathname + "/";
        // The client expects a 200's JSON, else an error carrying its status, which it logs.
        DockerRegistryClient.prototype.performHttpsGet = function (options) {
            return new Promise((resolve, reject) => {
                const target = new URL(under + options.hostname + options.path, place);
                const request = (target.protocol === "https:" ? https : http).get(target, { headers: options.headers }, (response) => {
                    if (response.statusCode !== 200) {
                        const error = new Error(response.statusMessage);
                        error.statusCode = response.statusCode;
                        response.resume();
                        reject(error);
                        return;
                    }
                    let body = "";
                    response.setEncoding("utf8");
                    response.on("data", (data) => {
                        body += data;
                    });
                    response.on("end", () => {
                        try {
                            resolve(JSON.parse(body));
                        } catch (error) {
                            reject(error);
                        }
                    });
                });
                request.on("error", reject);
            });
        };
    }
}
