#r "nuget: MongoDB.Driver, 2.25.0"
using MongoDB.Driver;
using MongoDB.Bson;

var client = new MongoClient("mongodb://localhost:27017");
var db = client.GetDatabase("hrmdb");
await db.DropCollectionAsync("users");
Console.WriteLine("Users collection dropped");
